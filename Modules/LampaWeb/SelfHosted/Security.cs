using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace SelfHosted;

public static class SelfHostedSecurity
{
    public const string CookieName = "lampac_session";
    static readonly ConcurrentDictionary<string, (int Count, DateTime Last)> LoginFailures = new();
    static readonly ConcurrentDictionary<string, Queue<DateTime>> AttemptWindows = new();

    public static string NormalizeEmail(string value) => (value ?? "").Trim().ToLowerInvariant();
    public static string NormalizeNickname(string value) => (value ?? "").Trim().ToLowerInvariant();
    public static string RandomToken(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static async Task LoginFailed(string key)
    {
        var state = LoginFailures.AddOrUpdate(key, _ => (1, DateTime.UtcNow), (_, old) => old.Last < DateTime.UtcNow.AddHours(-1) ? (1, DateTime.UtcNow) : (old.Count + 1, DateTime.UtcNow));
        int delay = state.Count <= 5 ? Random.Shared.Next(150, 350) : Math.Min(5_000, 250 * (1 << Math.Min(4, state.Count - 5)));
        await Task.Delay(delay);
    }

    public static void LoginSucceeded(string key) => LoginFailures.TryRemove(key, out _);

    public static bool TryConsume(string scope, string key, int permitLimit, TimeSpan window, out int retryAfterSeconds)
    {
        DateTime now = DateTime.UtcNow;
        var attempts = AttemptWindows.GetOrAdd(scope + "|" + key, _ => new Queue<DateTime>());
        lock (attempts)
        {
            while (attempts.Count > 0 && attempts.Peek() <= now - window)
                attempts.Dequeue();

            if (attempts.Count >= permitLimit)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((attempts.Peek() + window - now).TotalSeconds));
                return false;
            }

            attempts.Enqueue(now);
            retryAfterSeconds = 0;
            return true;
        }
    }

    public static string HashPassword(string password, int iterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$1${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string encoded)
    {
        try
        {
            string[] p = encoded.Split('$');
            if (p.Length != 5 || p[0] != "pbkdf2-sha256" || p[1] != "1") return false;
            int iterations = int.Parse(p[2]);
            byte[] salt = Convert.FromBase64String(p[3]);
            byte[] expected = Convert.FromBase64String(p[4]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    public static bool VerifyLoginPassword(string password, string? encoded, int iterations)
        => VerifyPassword(password, encoded ?? $"pbkdf2-sha256$1${iterations}$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    public static async Task<(UserRow User, SessionRow Session)?> Authenticate(HttpContext http, bool touch = true)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token)) return null;
        string hash = TokenHash(token);
        using var db = SelfHostedDb.Create();
        var session = await db.Sessions.FirstOrDefaultAsync(x => x.TokenHash == hash && x.RevokedAt == null);
        if (session == null || session.ExpiresAt <= DateTime.UtcNow || session.AbsoluteExpiresAt <= DateTime.UtcNow) return null;
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == session.UserId && x.Status == "active");
        if (user == null) return null;
        if (touch && session.LastSeenAt < DateTime.UtcNow.AddMinutes(-5))
        {
            session.LastSeenAt = DateTime.UtcNow;
            session.ExpiresAt = Min(DateTime.UtcNow.AddDays(SelfHostedRuntime.conf.session_idle_days), session.AbsoluteExpiresAt);
            db.Update(session);
            await db.SaveChangesAsync();
        }
        return (user, session);
    }

    public static async Task<(SessionRow Session, string Token)> CreateSession(UserRow user, HttpContext http, string? deviceName)
    {
        var created = BuildSession(user, http, deviceName);
        using var db = SelfHostedDb.Create();
        db.Sessions.Add(created.Session);
        await db.SaveChangesAsync();
        return created;
    }

    public static (SessionRow Session, string Token) BuildSession(UserRow user, HttpContext http, string? deviceName)
    {
        string token = RandomToken();
        DateTime absolute = DateTime.UtcNow.AddDays(SelfHostedRuntime.conf.session_absolute_days);
        var session = new SessionRow
        {
            UserId = user.Id,
            TokenHash = TokenHash(token),
            CsrfToken = RandomToken(24),
            DeviceName = CleanDeviceName(deviceName),
            UserAgent = http.Request.Headers.UserAgent.ToString()[..Math.Min(http.Request.Headers.UserAgent.ToString().Length, 500)],
            Ip = http.Connection.RemoteIpAddress?.ToString() ?? "",
            ExpiresAt = Min(DateTime.UtcNow.AddDays(SelfHostedRuntime.conf.session_idle_days), absolute),
            AbsoluteExpiresAt = absolute
        };
        return (session, token);
    }

    public static void SetCookie(HttpResponse response, string token) => response.Cookies.Append(CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = TimeSpan.FromDays(SelfHostedRuntime.conf.session_absolute_days),
        IsEssential = true
    });

    public static bool ValidCsrf(HttpRequest request, SessionRow session)
        => request.Headers.TryGetValue("X-CSRF-Token", out var value)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value.ToString()), Encoding.UTF8.GetBytes(session.CsrfToken));

    public static string CleanDeviceName(string? value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "Browser" : value.Trim();
        return name[..Math.Min(name.Length, 120)];
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
