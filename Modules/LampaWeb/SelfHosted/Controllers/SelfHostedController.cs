using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QRCoder;
using Shared;
using Shared.Attributes;
using System.Buffers;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SelfHosted.Controllers;

[Authorization("access denied")]
public sealed class SelfHostedController : BaseController
{
    static readonly SemaphoreSlim RevisionLock = new(1, 1);
    static readonly HttpClient PluginHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    static readonly HashSet<string> SyncKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "bookmark", "watch", "timeline", "setting", "plugin"
    };
    static readonly HashSet<string> SyncSettingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "favorite", "file_view", "recomends_list", "online_view", "torrents_view"
    };

    #region pages/plugins
    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH"), AllowAnonymous, Route("/cub/{*suffix}"), Route("/api/v1/cub-disabled")]
    public ActionResult CubGone() => Error(410, "cub_removed", "CUB отключён; используется SelfHosted");

    [HttpGet, AllowAnonymous, Route("/account")]
    public ActionResult AccountPage() => AccountHtml("");

    [HttpGet, AllowAnonymous, Route("/account/admin")]
    public ActionResult AdminPage() => Asset("admin.html", "text/html; charset=utf-8");

    [HttpGet, AllowAnonymous, Route("/account/pair/{secret}")]
    public ActionResult PairPage(string secret) => AccountHtml(secret);

    [HttpGet, AllowAnonymous, Route("/account/tmdb-logo.svg")]
    public ActionResult TmdbLogo() => Asset("tmdb-logo.svg", "image/svg+xml");

    #endregion

    #region auth
    [HttpPost, AllowAnonymous, EnableRateLimiting("selfhost-register"), Route("/api/v1/auth/register")]
    public async Task<ActionResult> Register([FromBody] RegisterRequest? body)
    {
        if (!SelfHostedSecurity.TryConsume("register-day", ClientIp(), 20, TimeSpan.FromDays(1), out int retryAfter))
            return RateLimited(retryAfter);
        if (body == null) return Error(400, "invalid_request", "Нужны email, ник и пароль");
        string email = SelfHostedSecurity.NormalizeEmail(body.Email);
        string nick = (body.Nickname ?? "").Trim();
        if (!ValidEmail(email) || nick.Length is < 2 or > 40 || !Regex.IsMatch(nick, @"^[\p{L}\p{N}_.-]+$"))
            return Error(400, "invalid_identity", "Проверьте email и ник");
        if (string.IsNullOrEmpty(body.Password) || body.Password.Length is < 12 or > 128)
            return Error(400, "weak_password", "Пароль должен содержать от 12 до 128 символов");

        using var db = SelfHostedDb.Create();
        if (await db.Users.AnyAsync(x => x.EmailNormalized == email || x.NicknameNormalized == SelfHostedSecurity.NormalizeNickname(nick)))
            return Error(409, "identity_unavailable", "Email или ник уже занят");

        var user = new UserRow
        {
            Email = email,
            EmailNormalized = email,
            Nickname = nick,
            NicknameNormalized = SelfHostedSecurity.NormalizeNickname(nick),
            PasswordHash = SelfHostedSecurity.HashPassword(body.Password, SelfHostedRuntime.conf.password_iterations)
        };
        db.Users.Add(user);
        db.Audit.Add(Audit(user.Id, "user.register"));
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Error(409, "identity_unavailable", "Email или ник уже занят"); }

        var created = await SelfHostedSecurity.CreateSession(user, HttpContext, body.DeviceName);
        SelfHostedSecurity.SetCookie(Response, created.Token);
        return OkData(new { user = PublicUser(user), csrfToken = created.Session.CsrfToken });
    }

    [HttpPost, AllowAnonymous, EnableRateLimiting("selfhost-login"), Route("/api/v1/auth/login")]
    public async Task<ActionResult> Login([FromBody] LoginRequest? body)
    {
        string email = SelfHostedSecurity.NormalizeEmail(body?.Email ?? "");
        string throttleKey = ClientIp() + "|" + email;
        if (!SelfHostedSecurity.TryConsume("login-quarter", throttleKey, 10, TimeSpan.FromMinutes(15), out int retryAfter))
            return RateLimited(retryAfter);
        using var db = SelfHostedDb.Create();
        var user = await db.Users.FirstOrDefaultAsync(x => x.EmailNormalized == email);
        bool active = user?.Status == "active";
        bool passwordValid = SelfHostedSecurity.VerifyLoginPassword(body?.Password ?? "", active ? user!.PasswordHash : null, SelfHostedRuntime.conf.password_iterations);
        if (!active || !passwordValid)
        {
            await SelfHostedSecurity.LoginFailed(throttleKey);
            return Error(401, "invalid_credentials", "Неверный email или пароль");
        }
        SelfHostedSecurity.LoginSucceeded(throttleKey);
        user.LastLoginAt = DateTime.UtcNow;
        db.Update(user);
        db.Audit.Add(Audit(user.Id, "user.login"));
        await db.SaveChangesAsync();
        var created = await SelfHostedSecurity.CreateSession(user, HttpContext, body?.DeviceName);
        SelfHostedSecurity.SetCookie(Response, created.Token);
        return OkData(new { user = PublicUser(user), csrfToken = created.Session.CsrfToken });
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/auth/me")]
    public async Task<ActionResult> Me()
    {
        var auth = await Auth();
        return auth == null ? Error(401, "unauthorized", "Требуется вход") : OkData(new { user = PublicUser(auth.Value.User), csrfToken = auth.Value.Session.CsrfToken });
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/auth/csrf")]
    public async Task<ActionResult> Csrf()
    {
        var auth = await Auth();
        return auth == null ? Error(401, "unauthorized", "Требуется вход") : OkData(new { csrfToken = auth.Value.Session.CsrfToken });
    }

    [HttpPost, AllowAnonymous, Route("/api/v1/auth/logout")]
    public async Task<ActionResult> Logout()
    {
        var auth = await RequireCsrf();
        if (auth.Result != null) return auth.Result;
        using var db = SelfHostedDb.Create();
        auth.Auth!.Value.Session.RevokedAt = DateTime.UtcNow;
        db.Update(auth.Auth.Value.Session);
        await db.SaveChangesAsync();
        Response.Cookies.Delete(SelfHostedSecurity.CookieName, new CookieOptions { Secure = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return OkData(new { loggedOut = true });
    }

    [HttpPost, AllowAnonymous, EnableRateLimiting("selfhost-login"), Route("/api/v1/auth/reset")]
    public async Task<ActionResult> ResetPassword([FromBody] ResetPasswordRequest? body)
    {
        if (body == null || string.IsNullOrEmpty(body.NewPassword) || body.NewPassword.Length is < 12 or > 128)
            return Error(400, "invalid_reset", "Проверьте reset-код и новый пароль");
        string hash = SelfHostedSecurity.TokenHash(body.Token ?? "");
        using var db = SelfHostedDb.Create();
        DateTime now = DateTime.UtcNow;
        var reset = await db.PasswordResets.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == hash && x.UsedAt == null && x.ExpiresAt > now);
        if (reset == null) return Error(400, "invalid_reset", "Reset-код недействителен или истёк");
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == reset.UserId && x.Status == "active");
        if (user == null) return Error(400, "invalid_reset", "Reset-код недействителен или истёк");
        using var tx = await db.Database.BeginTransactionAsync();
        int consumed = await db.PasswordResets.Where(x => x.Id == reset.Id && x.UsedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.UsedAt, now));
        if (consumed != 1) return Error(400, "invalid_reset", "Reset-код недействителен или истёк");
        user.PasswordHash = SelfHostedSecurity.HashPassword(body.NewPassword, SelfHostedRuntime.conf.password_iterations);
        await db.Sessions.Where(x => x.UserId == user.Id && x.RevokedAt == null).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTime.UtcNow));
        db.Update(user);
        db.Audit.Add(Audit(user.Id, "user.password.reset"));
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return OkData(new { changed = true });
    }
    #endregion

    #region pairing
    [HttpPost, AllowAnonymous, EnableRateLimiting("selfhost-pair"), Route("/api/v1/auth/pairings")]
    public async Task<ActionResult> CreatePairing([FromBody] PairingRequest? body)
    {
        string tvSecret = SelfHostedSecurity.RandomToken();
        string claimSecret = SelfHostedSecurity.RandomToken();
        var pairing = new PairingRow
        {
            TvSecretHash = SelfHostedSecurity.TokenHash(tvSecret),
            ClaimSecretHash = SelfHostedSecurity.TokenHash(claimSecret),
            ShortCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"),
            DeviceName = SelfHostedSecurity.CleanDeviceName(body?.DeviceName ?? "TV"),
            ExpiresAt = DateTime.UtcNow.AddMinutes(SelfHostedRuntime.conf.pairing_minutes)
        };
        using var db = SelfHostedDb.Create();
        for (int i = 0; i < 5 && await db.Pairings.AnyAsync(x => x.ShortCode == pairing.ShortCode); i++)
            pairing.ShortCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.Pairings.Add(pairing);
        await db.SaveChangesAsync();
        string root = string.IsNullOrWhiteSpace(SelfHostedRuntime.conf.public_url) ? host : SelfHostedRuntime.conf.public_url.TrimEnd('/');
        string claimUrl = $"{root}/account/pair/{claimSecret}";
        using var qrData = QRCodeGenerator.GenerateQrCode(claimUrl, QRCodeGenerator.ECCLevel.Q);
        using var qr = new SvgQRCode(qrData);
        return OkData(new { pairingId = pairing.Id, tvSecret, code = pairing.ShortCode, claimUrl, qrSvg = qr.GetGraphic(6), expiresAt = pairing.ExpiresAt });
    }

    [HttpGet, AllowAnonymous, EnableRateLimiting("selfhost-pair"), Route("/api/v1/auth/pairings/{id}/wait")]
    public async Task<ActionResult> WaitPairing(string id, [FromHeader(Name = "X-Pair-Token")] string tvSecret, CancellationToken ct)
    {
        string hash = SelfHostedSecurity.TokenHash(tvSecret ?? "");
        DateTime until = DateTime.UtcNow.AddSeconds(25);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < until)
        {
            using var db = SelfHostedDb.Create();
            var pairing = await db.Pairings.FirstOrDefaultAsync(x => x.Id == id && x.TvSecretHash == hash, ct);
            if (pairing == null) return Error(404, "pairing_not_found", "Сеанс входа не найден");
            if (pairing.ExpiresAt <= DateTime.UtcNow) return Error(410, "pairing_expired", "QR-код истёк");
            if (pairing.Status == "consumed" || pairing.ConsumedAt != null) return Error(410, "pairing_consumed", "QR-вход уже использован");
            if (pairing.Status == "claimed" && pairing.UserId != null && pairing.ConsumedAt == null)
            {
                string userId = pairing.UserId;
                using var tx = await db.Database.BeginTransactionAsync(ct);
                var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId && x.Status == "active", ct);
                if (user == null) return Error(410, "user_unavailable", "Аккаунт недоступен");
                var created = SelfHostedSecurity.BuildSession(user, HttpContext, pairing.DeviceName);
                int claimed = await db.Pairings
                    .Where(x => x.Id == pairing.Id && x.Status == "claimed" && x.ConsumedAt == null)
                    .ExecuteUpdateAsync(x => x
                        .SetProperty(p => p.Status, "consumed")
                        .SetProperty(p => p.ConsumedAt, DateTime.UtcNow), ct);
                if (claimed == 0) return Error(409, "pairing_consumed", "QR-вход уже использован");
                db.Sessions.Add(created.Session);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                SelfHostedSecurity.SetCookie(Response, created.Token);
                return OkData(new { authorized = true, user = PublicUser(user), csrfToken = created.Session.CsrfToken });
            }
            await Task.Delay(1000, ct);
        }
        return StatusCode(StatusCodes.Status204NoContent);
    }

    [HttpPost, AllowAnonymous, EnableRateLimiting("selfhost-pair"), Route("/api/v1/auth/pairings/claim"), Route("/api/v1/auth/pairings/{claim}/claim")]
    public async Task<ActionResult> ClaimPairing([FromBody] ClaimRequest? body, string? claim = null)
    {
        var auth = await RequireCsrf();
        if (auth.Result != null) return auth.Result;
        string secret = claim ?? body?.ClaimSecret ?? "";
        string code = (body?.Code ?? "").Trim();
        if (string.IsNullOrEmpty(secret) && !Regex.IsMatch(code, "^[0-9]{6}$"))
            return Error(400, "invalid_pairing_code", "Введите 6 цифр с экрана телевизора");
        string hash = SelfHostedSecurity.TokenHash(secret);
        using var db = SelfHostedDb.Create();
        var pairing = await db.Pairings.AsNoTracking().FirstOrDefaultAsync(x => (x.ClaimSecretHash == hash || (code != "" && x.ShortCode == code)) && x.Status == "pending");
        if (pairing == null || pairing.ExpiresAt <= DateTime.UtcNow) return Error(410, "pairing_expired", "QR-код недействителен");
        string userId = auth.Auth!.Value.User.Id;
        int claimed = await db.Pairings.Where(x => x.Id == pairing.Id && x.Status == "pending" && x.ExpiresAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.UserId, userId).SetProperty(p => p.Status, "claimed"));
        if (claimed == 0) return Error(410, "pairing_consumed", "QR-вход уже использован");
        db.Audit.Add(Audit(userId, "pairing.claim", new { pairing.DeviceName }));
        await db.SaveChangesAsync();
        return OkData(new { authorized = true, deviceName = pairing.DeviceName });
    }
    #endregion

    #region account
    [HttpGet, AllowAnonymous, Route("/api/v1/account/sessions")]
    public async Task<ActionResult> Sessions()
    {
        var auth = await Auth();
        if (auth == null) return Error(401, "unauthorized", "Требуется вход");
        using var db = SelfHostedDb.Create();
        var rows = await db.Sessions.AsNoTracking().Where(x => x.UserId == auth.Value.User.Id && x.RevokedAt == null && x.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(x => x.LastSeenAt).Select(x => new { x.Id, x.DeviceName, x.UserAgent, x.Ip, x.CreatedAt, x.LastSeenAt, x.ExpiresAt, current = x.Id == auth.Value.Session.Id }).ToListAsync();
        return OkData(rows);
    }

    [HttpDelete, AllowAnonymous, Route("/api/v1/account/sessions/{id}")]
    public async Task<ActionResult> RevokeSession(string id)
    {
        var auth = await RequireCsrf();
        if (auth.Result != null) return auth.Result;
        using var db = SelfHostedDb.Create();
        var row = await db.Sessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == auth.Auth!.Value.User.Id);
        if (row == null) return Error(404, "not_found", "Устройство не найдено");
        row.RevokedAt = DateTime.UtcNow;
        db.Update(row);
        await db.SaveChangesAsync();
        return OkData(new { revoked = true });
    }

    [HttpPatch, AllowAnonymous, Route("/api/v1/account/sessions/{id}")]
    public async Task<ActionResult> RenameSession(string id, [FromBody] RenameSessionRequest? body)
    {
        var auth = await RequireCsrf(); if (auth.Result != null) return auth.Result;
        string name = SelfHostedSecurity.CleanDeviceName(body?.DeviceName);
        using var db = SelfHostedDb.Create();
        var row = await db.Sessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == auth.Auth!.Value.User.Id && x.RevokedAt == null);
        if (row == null) return Error(404, "not_found", "Устройство не найдено");
        row.DeviceName = name; await db.SaveChangesAsync();
        return OkData(new { row.Id, row.DeviceName });
    }

    [HttpDelete, AllowAnonymous, Route("/api/v1/account/sessions")]
    public async Task<ActionResult> RevokeAllSessions()
    {
        var auth = await RequireCsrf(); if (auth.Result != null) return auth.Result;
        string userId = auth.Auth!.Value.User.Id;
        using var db = SelfHostedDb.Create();
        int revoked = await db.Sessions.Where(x => x.UserId == userId && x.RevokedAt == null).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTime.UtcNow));
        return OkData(new { revoked });
    }

    [HttpPost, AllowAnonymous, Route("/api/v1/account/password")]
    public async Task<ActionResult> ChangePassword([FromBody] PasswordRequest? body)
    {
        var auth = await RequireCsrf();
        if (auth.Result != null) return auth.Result;
        if (body == null || string.IsNullOrEmpty(body.NewPassword) || body.NewPassword.Length is < 12 or > 128 || !SelfHostedSecurity.VerifyPassword(body.CurrentPassword ?? "", auth.Auth!.Value.User.PasswordHash))
            return Error(400, "invalid_password", "Не удалось изменить пароль");
        using var db = SelfHostedDb.Create();
        var user = await db.Users.FirstAsync(x => x.Id == auth.Auth.Value.User.Id);
        user.PasswordHash = SelfHostedSecurity.HashPassword(body.NewPassword, SelfHostedRuntime.conf.password_iterations);
        await db.Sessions.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTime.UtcNow));
        db.Update(user);
        db.Audit.Add(Audit(user.Id, "user.password.change"));
        await db.SaveChangesAsync();
        return OkData(new { changed = true, loginRequired = true });
    }

    [HttpDelete, AllowAnonymous, Route("/api/v1/account"), Route("/api/v1/account/delete")]
    public async Task<ActionResult> DeleteAccount()
    {
        var auth = await RequireCsrf();
        if (auth.Result != null) return auth.Result;
        using var db = SelfHostedDb.Create();
        var user = await db.Users.FirstAsync(x => x.Id == auth.Auth!.Value.User.Id);
        user.Status = "pending_delete";
        user.DeleteAfter = DateTime.UtcNow.AddDays(30);
        await db.Sessions.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTime.UtcNow));
        db.Update(user);
        db.Audit.Add(Audit(user.Id, "user.delete.request"));
        await db.SaveChangesAsync();
        return OkData(new { deletedAt = user.DeleteAfter });
    }
    #endregion

    #region sync-backup
    [HttpGet, AllowAnonymous, Route("/api/v1/sync/bootstrap"), Route("/api/v1/sync/changes")]
    public async Task<ActionResult> SyncBootstrap([FromQuery] long after = 0)
    {
        var auth = await Auth();
        if (auth == null) return Error(401, "unauthorized", "Требуется вход");
        using var db = SelfHostedDb.Create();
        var rows = await db.SyncItems.AsNoTracking().Where(x => x.UserId == auth.Value.User.Id && x.Kind != "feed_state" && x.Revision > after).OrderBy(x => x.Revision).ToListAsync();
        var items = rows.Select(x => new { x.Kind, key = x.ItemKey, data = JToken.Parse(x.Data), x.Revision, x.Deleted, x.UpdatedAt }).ToList();
        return OkData(new { items, revision = items.Count == 0 ? after : items.Max(x => x.Revision) });
    }

    [HttpPut, AllowAnonymous, Route("/api/v1/sync/item"), Route("/api/v1/sync/bookmarks"), Route("/api/v1/sync/timelines"), Route("/api/v1/sync/settings"), Route("/api/v1/sync/plugins")]
    public async Task<ActionResult> SyncPut([FromBody] SyncPutRequest? body)
    {
        var auth = await RequireCsrf();
        if (auth.Result != null) return auth.Result;
        string userId = auth.Auth!.Value.User.Id;
        string path = Request.Path.Value ?? "";
        if (body != null && !path.EndsWith("/item", StringComparison.OrdinalIgnoreCase))
            body = body with { Kind = path.EndsWith("/bookmarks", StringComparison.OrdinalIgnoreCase) ? "bookmark" : path.EndsWith("/timelines", StringComparison.OrdinalIgnoreCase) ? "timeline" : path.EndsWith("/plugins", StringComparison.OrdinalIgnoreCase) ? "plugin" : "setting" };
        if (body == null || !SyncKinds.Contains(body.Kind) || string.IsNullOrWhiteSpace(body.Key) || body.Key.Length > 300)
            return Error(400, "invalid_sync_item", "Некорректный элемент синхронизации");
        if (body.Kind.Equals("setting", StringComparison.OrdinalIgnoreCase) && !SyncSettingKeys.Contains(body.Key))
            return Error(400, "setting_not_allowed", "Эта настройка остаётся только на устройстве");
        string data = JsonConvert.SerializeObject(body.Data ?? new { });
        if (Encoding.UTF8.GetByteCount(data) > SelfHostedRuntime.conf.backup_max_mb * 1024 * 1024)
            return Error(413, "item_too_large", $"Элемент превышает {SelfHostedRuntime.conf.backup_max_mb} МБ");
        string? idempotencyKey = string.IsNullOrWhiteSpace(body.IdempotencyKey) ? null : body.IdempotencyKey.Trim();
        if (idempotencyKey?.Length > 120) return Error(400, "invalid_idempotency_key", "Idempotency key слишком длинный");
        await RevisionLock.WaitAsync();
        try
        {
            using var db = SelfHostedDb.Create();
            if (idempotencyKey != null)
            {
                var previous = await db.Mutations.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.IdempotencyKey == idempotencyKey);
                if (previous != null) return OkData(new { previous.Revision, duplicate = true });
            }
            using var tx = await db.Database.BeginTransactionAsync();
            long revision = (await db.SyncItems.Where(x => x.UserId == userId).MaxAsync(x => (long?)x.Revision) ?? 0) + 1;
            var row = await db.SyncItems.FirstOrDefaultAsync(x => x.UserId == userId && x.Kind == body.Kind && x.ItemKey == body.Key);
            long currentBytes = row?.Deleted == false ? Encoding.UTF8.GetByteCount(row.Data) : 0;
            long usedBytes = await db.SyncItems.Where(x => x.UserId == userId && !x.Deleted).SumAsync(x => (long?)x.Data.Length) ?? 0;
            int itemCount = await db.SyncItems.CountAsync(x => x.UserId == userId && !x.Deleted);
            long maxBytes = (long)SelfHostedRuntime.conf.backup_max_mb * 1024 * 1024;
            if (usedBytes - currentBytes + Encoding.UTF8.GetByteCount(data) > maxBytes || (row == null && itemCount >= 20_000))
                return Error(413, "sync_quota", $"Лимит синхронизации — {SelfHostedRuntime.conf.backup_max_mb} МБ и 20 000 элементов");
            if (row == null) db.SyncItems.Add(row = new SyncItemRow { UserId = userId, Kind = body.Kind.ToLowerInvariant(), ItemKey = body.Key });
            row.Data = data; row.Revision = revision; row.Deleted = false; row.UpdatedAt = DateTime.UtcNow;
            if (idempotencyKey != null) db.Mutations.Add(new MutationRow { UserId = userId, IdempotencyKey = idempotencyKey, Revision = revision });
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return OkData(new { row.Revision, row.UpdatedAt, duplicate = false });
        }
        finally { RevisionLock.Release(); }
    }

    [HttpDelete, AllowAnonymous, Route("/api/v1/sync/item")]
    public async Task<ActionResult> SyncDelete([FromQuery] string kind, [FromQuery] string key)
    {
        var auth = await RequireCsrf();
        if (auth.Result != null) return auth.Result;
        string userId = auth.Auth!.Value.User.Id;
        kind = (kind ?? "").Trim().ToLowerInvariant(); key = (key ?? "").Trim();
        if (!SyncKinds.Contains(kind) || string.IsNullOrWhiteSpace(key) || key.Length > 300)
            return Error(400, "invalid_sync_item", "Некорректный элемент синхронизации");
        await RevisionLock.WaitAsync();
        try
        {
            using var db = SelfHostedDb.Create();
            using var tx = await db.Database.BeginTransactionAsync();
            var row = await db.SyncItems.FirstOrDefaultAsync(x => x.UserId == userId && x.Kind == kind && x.ItemKey == key);
            if (row?.Deleted == true) return OkData(new { deleted = true, row.Revision, duplicate = true });
            long revision = (await db.SyncItems.Where(x => x.UserId == userId).MaxAsync(x => (long?)x.Revision) ?? 0) + 1;
            if (row == null) db.SyncItems.Add(row = new SyncItemRow { UserId = userId, Kind = kind, ItemKey = key, Data = "null" });
            row.Deleted = true; row.Revision = revision; row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return OkData(new { deleted = true, revision, duplicate = false });
        }
        finally { RevisionLock.Release(); }
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/backup/export")]
    public async Task<ActionResult> BackupExport()
    {
        var auth = await Auth();
        if (auth == null) return Error(401, "unauthorized", "Требуется вход");
        using var db = SelfHostedDb.Create();
        var rows = await db.SyncItems.AsNoTracking().Where(x => x.UserId == auth.Value.User.Id && x.Kind != "feed_state" && !x.Deleted).ToListAsync();
        var items = rows.Select(x => new { x.Kind, key = x.ItemKey, data = JToken.Parse(x.Data) }).ToList();
        byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { version = 1, exportedAt = DateTime.UtcNow, items }));
        if (payload.Length > SelfHostedRuntime.conf.backup_max_mb * 1024 * 1024)
            return Error(413, "backup_too_large", $"Backup превышает {SelfHostedRuntime.conf.backup_max_mb} МБ");
        return File(payload, "application/json", "lampac-backup.json");
    }

    [HttpPost, AllowAnonymous, RequestSizeLimit(10 * 1024 * 1024), Route("/api/v1/backup/import"), Route("/api/v1/backup/import/preview")]
    public async Task<ActionResult> BackupImport([FromBody] JObject? backup, [FromQuery] bool preview = false)
    {
        var auth = await RequireCsrf();
        if (auth.Result != null) return auth.Result;
        string userId = auth.Auth!.Value.User.Id;
        var items = backup?["items"] as JArray;
        if (backup?.Value<int>("version") != 1 || items == null || items.Count > 20_000) return Error(400, "invalid_backup", "Некорректный backup");
        var validated = new List<(string Kind, string Key, string Data)>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in items.OfType<JObject>())
        {
            string kind = (token.Value<string>("kind") ?? "").Trim().ToLowerInvariant();
            string key = (token.Value<string>("key") ?? "").Trim();
            string data = (token["data"] ?? JValue.CreateNull()).ToString(Formatting.None);
            if (!SyncKinds.Contains(kind) || string.IsNullOrWhiteSpace(key) || key.Length > 300
                || (kind == "setting" && !SyncSettingKeys.Contains(key))
                || Encoding.UTF8.GetByteCount(data) > SelfHostedRuntime.conf.backup_max_mb * 1024 * 1024
                || !unique.Add(kind + "\n" + key))
                return Error(400, "invalid_backup_item", "Backup содержит некорректный или повторяющийся элемент");
            validated.Add((kind, key, data));
        }
        long importBytes = validated.Sum(x => (long)Encoding.UTF8.GetByteCount(x.Data));
        if (importBytes > (long)SelfHostedRuntime.conf.backup_max_mb * 1024 * 1024)
            return Error(413, "backup_too_large", $"Backup превышает {SelfHostedRuntime.conf.backup_max_mb} МБ");
        if (preview || Request.Path.Value?.EndsWith("/preview", StringComparison.OrdinalIgnoreCase) == true)
            return OkData(new { valid = true, items = validated.Count, bytes = importBytes });

        await RevisionLock.WaitAsync();
        try
        {
            using var db = SelfHostedDb.Create();
            using var tx = await db.Database.BeginTransactionAsync();
            var rows = await db.SyncItems.Where(x => x.UserId == userId && x.Kind != "feed_state").ToListAsync();
            long revision = await db.SyncItems.Where(x => x.UserId == userId).MaxAsync(x => (long?)x.Revision) ?? 0;
            foreach (var row in rows.Where(x => !x.Deleted))
            {
                row.Deleted = true; row.Revision = ++revision; row.UpdatedAt = DateTime.UtcNow;
            }
            var byKey = rows.ToDictionary(x => x.Kind + "\n" + x.ItemKey, StringComparer.Ordinal);
            foreach (var item in validated)
            {
                if (!byKey.TryGetValue(item.Kind + "\n" + item.Key, out var row))
                {
                    row = new SyncItemRow { UserId = userId, Kind = item.Kind, ItemKey = item.Key };
                    db.SyncItems.Add(row);
                    byKey[item.Kind + "\n" + item.Key] = row;
                }
                row.Data = item.Data; row.Deleted = false; row.Revision = ++revision; row.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return OkData(new { imported = validated.Count, revision });
        }
        finally { RevisionLock.Release(); }
    }
    #endregion

    #region community
    [HttpGet, AllowAnonymous, Route("/api/v1/community/reactions")]
    public async Task<ActionResult> GetReactions([FromQuery] string type, [FromQuery] long tmdbId)
    {
        var auth = await Auth();
        using var db = SelfHostedDb.Create();
        var rows = db.Reactions.AsNoTracking().Where(x => x.ContentType == type && x.TmdbId == tmdbId);
        return OkData(new { likes = await rows.CountAsync(x => x.Value == 1), dislikes = await rows.CountAsync(x => x.Value == -1), mine = auth == null ? 0 : await rows.Where(x => x.UserId == auth.Value.User.Id).Select(x => x.Value).FirstOrDefaultAsync() });
    }

    [HttpPut, AllowAnonymous, EnableRateLimiting("selfhost-community"), Route("/api/v1/community/reactions")]
    public async Task<ActionResult> SetReaction([FromBody] ReactionRequest? body)
    {
        var auth = await RequireCsrf(); if (auth.Result != null) return auth.Result;
        string userId = auth.Auth!.Value.User.Id;
        if (!SelfHostedSecurity.TryConsume("community-minute", userId, 30, TimeSpan.FromMinutes(1), out int retryAfter))
            return RateLimited(retryAfter);
        if (body == null || body.Type is not ("movie" or "tv") || body.TmdbId <= 0 || body.Value is < -1 or > 1) return Error(400, "invalid_reaction", "Некорректная реакция");
        using var db = SelfHostedDb.Create();
        var row = await db.Reactions.FirstOrDefaultAsync(x => x.UserId == userId && x.ContentType == body.Type && x.TmdbId == body.TmdbId);
        if (body.Value == 0) { if (row != null) db.Remove(row); }
        else if (row == null) db.Reactions.Add(new ReactionRow { UserId = userId, ContentType = body.Type, TmdbId = body.TmdbId, Value = body.Value });
        else { row.Value = body.Value; row.UpdatedAt = DateTime.UtcNow; db.Update(row); }
        await db.SaveChangesAsync();
        return await GetReactions(body.Type, body.TmdbId);
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/subscriptions")]
    public async Task<ActionResult> Subscriptions()
    {
        var auth = await Auth(); if (auth == null) return Error(401, "unauthorized", "Требуется вход");
        using var db = SelfHostedDb.Create();
        return OkData(await db.Subscriptions.AsNoTracking().Where(x => x.UserId == auth.Value.User.Id).OrderByDescending(x => x.CreatedAt).ToListAsync());
    }

    [HttpPut, AllowAnonymous, EnableRateLimiting("selfhost-community"), Route("/api/v1/subscriptions")]
    public async Task<ActionResult> SetSubscription([FromBody] SubscriptionRequest? body)
    {
        var auth = await RequireCsrf(); if (auth.Result != null) return auth.Result;
        string userId = auth.Auth!.Value.User.Id;
        if (!SelfHostedSecurity.TryConsume("community-minute", userId, 30, TimeSpan.FromMinutes(1), out int retryAfter))
            return RateLimited(retryAfter);
        if (body == null || body.Type is not ("movie" or "tv" or "person") || body.TmdbId <= 0) return Error(400, "invalid_subscription", "Некорректная подписка");
        using var db = SelfHostedDb.Create();
        var row = await db.Subscriptions.FirstOrDefaultAsync(x => x.UserId == userId && x.ContentType == body.Type && x.TmdbId == body.TmdbId);
        if (body.Enabled && row == null) db.Subscriptions.Add(new SubscriptionRow { UserId = userId, ContentType = body.Type, TmdbId = body.TmdbId });
        if (!body.Enabled && row != null) db.Remove(row);
        await db.SaveChangesAsync(); return OkData(new { body.Enabled });
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/feed"), Route("/api/v1/notifications")]
    public async Task<ActionResult> Feed()
    {
        var auth = await Auth(); if (auth == null) return Error(401, "unauthorized", "Требуется вход");
        using var db = SelfHostedDb.Create();
        return OkData(await db.Notifications.AsNoTracking().Where(x => x.UserId == auth.Value.User.Id && x.CreatedAt > DateTime.UtcNow.AddDays(-90)).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync());
    }

    [HttpPatch, AllowAnonymous, Route("/api/v1/notifications/{id}/read")]
    public async Task<ActionResult> ReadNotification(long id)
    {
        var auth = await RequireCsrf(); if (auth.Result != null) return auth.Result;
        int changed;
        using (var db = SelfHostedDb.Create())
            changed = await db.Notifications.Where(x => x.Id == id && x.UserId == auth.Auth!.Value.User.Id).ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAt, DateTime.UtcNow));
        return changed == 0 ? Error(404, "not_found", "Уведомление не найдено") : OkData(new { read = true });
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/catalog/plugins")]
    public async Task<ActionResult> PluginCatalog()
    {
        var auth = await Auth();
        using var db = SelfHostedDb.Create();
        var plugins = await db.Plugins.AsNoTracking().Where(x => x.Enabled && !x.Blacklisted).OrderBy(x => x.Name).ToListAsync();
        var ratings = await db.PluginRatings.AsNoTracking().ToListAsync();
        var installs = await db.PluginInstalls.AsNoTracking().ToListAsync();
        return OkData(plugins.Select(x => new { x.Id, x.Name, x.Url, x.Author, x.Description, x.Version, x.Compatibility, x.Sha256, x.VerificationStatus, rating = ratings.Where(r => r.PluginId == x.Id).Select(r => (double?)r.Value).Average() ?? 0, installs = installs.Count(i => i.PluginId == x.Id), installed = auth != null && installs.Any(i => i.PluginId == x.Id && i.UserId == auth.Value.User.Id) }));
    }

    [AcceptVerbs("PUT", "DELETE"), AllowAnonymous, Route("/api/v1/catalog/plugins/{id}/install")]
    public async Task<ActionResult> InstallPlugin(long id)
    {
        var auth = await RequireCsrf(); if (auth.Result != null) return auth.Result;
        string userId = auth.Auth!.Value.User.Id;
        using var db = SelfHostedDb.Create();
        if (!await db.Plugins.AnyAsync(x => x.Id == id && x.Enabled && !x.Blacklisted)) return Error(404, "not_found", "Плагин не найден");
        var row = await db.PluginInstalls.FirstOrDefaultAsync(x => x.PluginId == id && x.UserId == userId);
        bool enabled = !HttpMethods.IsDelete(Request.Method);
        if (enabled && row == null) db.PluginInstalls.Add(new PluginInstallRow { PluginId = id, UserId = userId });
        if (!enabled && row != null) db.PluginInstalls.Remove(row);
        await db.SaveChangesAsync();
        return OkData(new { installed = enabled });
    }

    [HttpPut, AllowAnonymous, Route("/api/v1/catalog/plugins/{id}/rating")]
    public async Task<ActionResult> RatePlugin(long id, [FromBody] PluginRatingRequest? body)
    {
        var auth = await RequireCsrf(); if (auth.Result != null) return auth.Result;
        if (body == null || body.Value is < 1 or > 5) return Error(400, "invalid_rating", "Оценка должна быть от 1 до 5");
        string userId = auth.Auth!.Value.User.Id;
        using var db = SelfHostedDb.Create();
        if (!await db.Plugins.AnyAsync(x => x.Id == id && x.Enabled && !x.Blacklisted)) return Error(404, "not_found", "Плагин не найден");
        var row = await db.PluginRatings.FirstOrDefaultAsync(x => x.PluginId == id && x.UserId == userId);
        if (row == null) db.PluginRatings.Add(new PluginRatingRow { PluginId = id, UserId = userId, Value = body.Value }); else row.Value = body.Value;
        await db.SaveChangesAsync();
        double average = await db.PluginRatings.Where(x => x.PluginId == id).AverageAsync(x => x.Value);
        return OkData(new { rating = average });
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/search/smart")]
    public ActionResult SmartSearch([FromQuery] string query, [FromQuery] string language = "ru-RU")
    {
        query = (query ?? "").Trim();
        if (query.Length is < 2 or > 200) return Error(400, "invalid_query", "Введите запрос");
        var year = Regex.Match(query, @"\b(19|20)\d{2}\b");
        string clean = year.Success ? query.Replace(year.Value, "").Trim() : query;
        string url = $"/tmdb/api/3/search/multi?api_key=4ef0d7355d9ffb5151e987764708ce96&query={Uri.EscapeDataString(clean)}&language={Uri.EscapeDataString(language)}&include_adult=false" + (year.Success ? $"&year={year.Value}" : "");
        return OkData(new { url, parsed = new { text = clean, year = year.Success ? int.Parse(year.Value) : (int?)null } });
    }
    #endregion

    #region admin
    [HttpGet, AllowAnonymous, Route("/api/v1/admin/users")]
    public async Task<ActionResult> AdminUsers([FromQuery] string? q = null)
    {
        var admin = await RequireAdmin(); if (admin.Result != null) return admin.Result;
        using var db = SelfHostedDb.Create();
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q)) { string s = q.Trim().ToLowerInvariant(); query = query.Where(x => x.EmailNormalized.Contains(s) || x.NicknameNormalized.Contains(s)); }
        return OkData(await query.OrderByDescending(x => x.CreatedAt).Take(500).Select(x => new { x.Id, x.Email, x.Nickname, x.Role, x.Status, x.CreatedAt, x.LastLoginAt, x.DeleteAfter }).ToListAsync());
    }

    [HttpPost, AllowAnonymous, Route("/api/v1/admin/users/{id}/reset-token")]
    public async Task<ActionResult> AdminReset(string id)
    {
        var admin = await RequireAdmin(true); if (admin.Result != null) return admin.Result;
        string token = SelfHostedSecurity.RandomToken(24);
        using var db = SelfHostedDb.Create();
        if (!await db.Users.AnyAsync(x => x.Id == id)) return Error(404, "not_found", "Пользователь не найден");
        await db.PasswordResets.Where(x => x.UserId == id && x.UsedAt == null).ExecuteUpdateAsync(x => x.SetProperty(r => r.UsedAt, DateTime.UtcNow));
        db.PasswordResets.Add(new PasswordResetRow { UserId = id, TokenHash = SelfHostedSecurity.TokenHash(token), ExpiresAt = DateTime.UtcNow.AddMinutes(15) });
        db.Audit.Add(Audit(admin.Auth!.Value.User.Id, "admin.reset.create", new { target = id }));
        await db.SaveChangesAsync(); return OkData(new { token, expiresInSeconds = 900 });
    }

    [HttpPatch, AllowAnonymous, Route("/api/v1/admin/users/{id}")]
    public async Task<ActionResult> AdminUser(string id, [FromBody] AdminUserRequest? body)
    {
        var admin = await RequireAdmin(true); if (admin.Result != null) return admin.Result;
        if (body?.Action is not ("block" or "unblock" or "delete")) return Error(400, "invalid_action", "Неизвестное действие");
        using var db = SelfHostedDb.Create();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (user == null) return Error(404, "not_found", "Пользователь не найден");
        if (user.Id == admin.Auth!.Value.User.Id && body.Action != "unblock") return Error(400, "self_action", "Нельзя заблокировать текущего администратора");
        user.Status = body.Action == "unblock" ? "active" : body.Action == "delete" ? "pending_delete" : "blocked";
        user.DeleteAfter = body.Action == "delete" ? DateTime.UtcNow.AddDays(30) : null;
        if (body.Action != "unblock") await db.Sessions.Where(x => x.UserId == id && x.RevokedAt == null).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTime.UtcNow));
        db.Audit.Add(Audit(admin.Auth.Value.User.Id, "admin.user." + body.Action, new { target = id }));
        await db.SaveChangesAsync(); return OkData(new { user.Status, user.DeleteAfter });
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/admin/sessions")]
    public async Task<ActionResult> AdminSessions([FromQuery] string? userId = null)
    {
        var admin = await RequireAdmin(); if (admin.Result != null) return admin.Result;
        using var db = SelfHostedDb.Create();
        var query = db.Sessions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(userId)) query = query.Where(x => x.UserId == userId);
        return OkData(await query.OrderByDescending(x => x.LastSeenAt).Take(500).Select(x => new { x.Id, x.UserId, x.DeviceName, x.Ip, x.CreatedAt, x.LastSeenAt, x.ExpiresAt, x.RevokedAt }).ToListAsync());
    }

    [HttpDelete, AllowAnonymous, Route("/api/v1/admin/sessions/{id}")]
    public async Task<ActionResult> AdminRevokeSession(string id)
    {
        var admin = await RequireAdmin(true); if (admin.Result != null) return admin.Result;
        using var db = SelfHostedDb.Create();
        int changed = await db.Sessions.Where(x => x.Id == id && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, DateTime.UtcNow));
        db.Audit.Add(Audit(admin.Auth!.Value.User.Id, "admin.session.revoke", new { session = id }));
        await db.SaveChangesAsync();
        return changed == 0 ? Error(404, "not_found", "Сессия не найдена") : OkData(new { revoked = true });
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/admin/plugins")]
    public async Task<ActionResult> AdminPlugins()
    {
        var admin = await RequireAdmin(); if (admin.Result != null) return admin.Result;
        using var db = SelfHostedDb.Create();
        return OkData(await db.Plugins.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }

    [HttpPost, AllowAnonymous, Route("/api/v1/admin/plugins")]
    public async Task<ActionResult> AdminPlugin([FromBody] PluginRow? plugin)
    {
        var admin = await RequireAdmin(true); if (admin.Result != null) return admin.Result;
        if (plugin == null || string.IsNullOrWhiteSpace(plugin.Name) || plugin.Name.Trim().Length > 120
            || !Uri.TryCreate(plugin.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return Error(400, "invalid_plugin", "Нужны название и публичный HTTPS URL");
        byte[] script;
        try { script = await DownloadPlugin(uri, 2 * 1024 * 1024); }
        catch { return Error(400, "plugin_unreachable", "Не удалось скачать плагин для проверки"); }
        if (script.Length == 0) return Error(400, "invalid_plugin_size", "Размер плагина должен быть до 2 МБ");
        string sha256 = Convert.ToHexString(SHA256.HashData(script)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(plugin.Sha256) && !sha256.Equals(plugin.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)) return Error(400, "sha256_mismatch", "SHA-256 плагина не совпадает");
        using var db = SelfHostedDb.Create();
        var row = plugin.Id > 0 ? await db.Plugins.FirstOrDefaultAsync(x => x.Id == plugin.Id) : await db.Plugins.FirstOrDefaultAsync(x => x.Url == plugin.Url);
        if (row == null) db.Plugins.Add(row = new PluginRow());
        row.Name = plugin.Name.Trim(); row.Url = plugin.Url.Trim(); row.Author = plugin.Author?.Trim() ?? ""; row.Description = plugin.Description?.Trim() ?? "";
        row.Version = plugin.Version?.Trim() ?? ""; row.Compatibility = plugin.Compatibility?.Trim() ?? ""; row.Sha256 = sha256; row.VerificationStatus = "verified"; row.Enabled = plugin.Enabled; row.Blacklisted = plugin.Blacklisted; row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); return OkData(row);
    }

    [HttpGet, AllowAnonymous, Route("/api/v1/admin/audit")]
    public async Task<ActionResult> AdminAudit()
    {
        var admin = await RequireAdmin(); if (admin.Result != null) return admin.Result;
        using var db = SelfHostedDb.Create(); return OkData(await db.Audit.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync());
    }
    #endregion

    async Task<(UserRow User, SessionRow Session)?> Auth() => HttpContext.Items.TryGetValue("selfhosted.auth", out var value) && value is ValueTuple<UserRow, SessionRow> auth ? auth : await SelfHostedSecurity.Authenticate(HttpContext);

    async Task<((UserRow User, SessionRow Session)? Auth, ActionResult? Result)> RequireCsrf()
    {
        var auth = await Auth();
        if (auth == null) return (null, Error(401, "unauthorized", "Требуется вход"));
        if (!SelfHostedSecurity.ValidCsrf(Request, auth.Value.Session)) return (null, Error(403, "csrf", "Обновите страницу и повторите"));
        return (auth, null);
    }

    async Task<((UserRow User, SessionRow Session)? Auth, ActionResult? Result)> RequireAdmin(bool csrf = false)
    {
        var result = csrf ? await RequireCsrf() : (await Auth(), (ActionResult?)null);
        if (result.Item2 != null) return result;
        if (result.Item1 == null) return (null, Error(401, "unauthorized", "Требуется вход"));
        if (result.Item1.Value.User.Role != "admin") return (null, Error(403, "forbidden", "Нужны права администратора"));
        return result;
    }

    ActionResult Asset(string name, string type) => File(Encoding.UTF8.GetBytes(System.IO.File.ReadAllText(Path.Combine(SelfHostedRuntime.modpath, "Assets", name), Encoding.UTF8)), type);
    ActionResult AccountHtml(string secret) => File(Encoding.UTF8.GetBytes(System.IO.File.ReadAllText(Path.Combine(SelfHostedRuntime.modpath, "Assets", "account.html"), Encoding.UTF8).Replace("\"__PAIR_SECRET__\"", Js(secret))), "text/html; charset=utf-8");
    ActionResult OkData(object data) => Json(new { ok = true, data, requestId = HttpContext.TraceIdentifier });
    ActionResult Error(int status, string code, string message) { Response.StatusCode = status; return Json(new { ok = false, code, message, requestId = HttpContext.TraceIdentifier }); }
    ActionResult RateLimited(int retryAfterSeconds)
    {
        Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        return Error(429, "rate_limited", "Слишком много попыток. Повторите позже");
    }
    string ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    static async Task<byte[]> DownloadPlugin(Uri initial, int maxBytes)
    {
        Uri current = initial;
        for (int redirect = 0; redirect <= 3; redirect++)
        {
            await EnsurePublicHttps(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await PluginHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirect == 3 || response.Headers.Location == null) throw new HttpRequestException("redirect");
                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes) throw new HttpRequestException("too large");
            await using var input = await response.Content.ReadAsStreamAsync();
            using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer);
                    if (read == 0) break;
                    if (output.Length + read > maxBytes) throw new HttpRequestException("too large");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        throw new HttpRequestException("redirect");
    }

    static async Task EnsurePublicHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port is <= 0 or > 65535)
            throw new HttpRequestException("invalid uri");
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost);
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
            throw new HttpRequestException("private address");
    }

    static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (b.Length == 4)
            return b[0] is 0 or 10 or 127
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224;
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xfe) == 0xfc;
    }
    AuditRow Audit(string? userId, string action, object? data = null) => new() { UserId = userId, Action = action, Data = JsonConvert.SerializeObject(data ?? new { }), Ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "" };
    static object PublicUser(UserRow user) => new { user.Id, user.Email, user.Nickname, user.Role, user.CreatedAt };
    static bool ValidEmail(string email) { try { return new MailAddress(email).Address.Equals(email, StringComparison.OrdinalIgnoreCase) && email.Length <= 254; } catch { return false; } }
    static string Js(string value) => JsonConvert.SerializeObject(value ?? "");
}
