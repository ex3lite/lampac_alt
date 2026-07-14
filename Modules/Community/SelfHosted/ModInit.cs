using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Shared.Models.AppConf;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Services;

namespace SelfHosted;

public sealed class ModInit : IModuleLoaded, IModuleConfigure
{
    public static string modpath = "";
    public static SelfHostedConf conf = new();
    Timer? maintenanceTimer;
    static readonly HttpClient FeedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly SemaphoreSlim FeedLock = new(1, 1);
    static DateTime lastFeedCheck;

    public void Configure(ConfigureModel app) => app.services.AddDbContextFactory<SelfHostedDb>(SelfHostedDb.Configure);

    public void Loaded(InitspaceModel init)
    {
        modpath = init.path;
        Reload();
        EventListener.UpdateInitFile += Reload;
        EventListener.MiddlewareAsync += IdentityMiddleware;
        SelfHostedDb.Initialize(init.app.ApplicationServices);
        BootstrapAdmin();
        Maintenance();
        maintenanceTimer = new Timer(_ => Maintenance(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    void Reload() => conf = ModuleInvoke.Init("SelfHosted", new SelfHostedConf
    {
        enable = true,
        password_iterations = 600_000,
        session_idle_days = 30,
        session_absolute_days = 180,
        pairing_minutes = 5,
        backup_max_mb = 10,
        limit_map = new List<WafLimitRootMap> { new("^/api/v1/", new WafLimitMap { limit = 60, second = 1 }) }
    });

    async Task<bool> IdentityMiddleware(bool first, EventMiddleware e)
    {
        if (!first || !conf.enable) return true;
        var auth = await SelfHostedSecurity.Authenticate(e.httpContext);
        if (auth != null)
        {
            var requestInfo = e.httpContext.Features.Get<Shared.Models.Base.RequestModel>();
            if (requestInfo != null) requestInfo.user_uid = auth.Value.User.Id;
            e.httpContext.Items["selfhosted.auth"] = auth.Value;
        }
        return true;
    }

    void BootstrapAdmin()
    {
        if (string.IsNullOrWhiteSpace(conf.admin_email) || string.IsNullOrWhiteSpace(conf.admin_bootstrap_secret)) return;
        using var db = SelfHostedDb.Create();
        string email = SelfHostedSecurity.NormalizeEmail(conf.admin_email);
        if (db.Users.Any(x => x.EmailNormalized == email)) return;
        db.Users.Add(new UserRow
        {
            Email = email,
            EmailNormalized = email,
            Nickname = "admin",
            NicknameNormalized = "admin",
            PasswordHash = SelfHostedSecurity.HashPassword(conf.admin_bootstrap_secret, conf.password_iterations),
            Role = "admin"
        });
        db.SaveChanges();
    }

    void Maintenance()
    {
        try
        {
            using var db = SelfHostedDb.Create();
            var expiredUsers = db.Users.Where(x => x.Status == "pending_delete" && x.DeleteAfter <= DateTime.UtcNow).Select(x => x.Id).ToList();
            foreach (string userId in expiredUsers)
            {
                using var tx = db.Database.BeginTransaction();
                db.Database.ExecuteSqlInterpolated($"DELETE FROM sessions WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM pairings WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM sync_items WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM reactions WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM subscriptions WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM notifications WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM plugin_ratings WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM plugin_installs WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM password_reset_tokens WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM mutations WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"UPDATE audit_log SET UserId = NULL WHERE UserId = {userId}");
                db.Database.ExecuteSqlInterpolated($"DELETE FROM users WHERE Id = {userId}");
                tx.Commit();
            }
            db.PasswordResets.Where(x => x.ExpiresAt < DateTime.UtcNow.AddDays(-1)).ExecuteDelete();
            db.Pairings.Where(x => x.ExpiresAt < DateTime.UtcNow.AddDays(-1)).ExecuteDelete();
            db.Notifications.Where(x => x.CreatedAt < DateTime.UtcNow.AddDays(-90)).ExecuteDelete();

            string backupDir = Path.Combine("database", "backups");
            Directory.CreateDirectory(backupDir);
            string backup = Path.Combine(backupDir, $"SelfHosted-{DateTime.UtcNow:yyyyMMdd}.sql");
            if (!File.Exists(backup))
            {
                using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SelfHostedDb.DbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup }.ToString());
                source.Open(); destination.Open(); source.BackupDatabase(destination);
            }
            foreach (string old in Directory.GetFiles(backupDir, "SelfHosted-*.sql").Where(x => File.GetLastWriteTimeUtc(x) < DateTime.UtcNow.AddDays(-14)))
                File.Delete(old);

            if (!string.IsNullOrWhiteSpace(conf.public_url) && lastFeedCheck < DateTime.UtcNow.AddHours(-6))
            {
                lastFeedCheck = DateTime.UtcNow;
                _ = RefreshSubscriptions();
            }
        }
        catch (Exception ex) { Console.WriteLine($"SelfHosted maintenance: {ex.Message}"); }
    }

    async Task RefreshSubscriptions()
    {
        if (!await FeedLock.WaitAsync(0)) return;
        try
        {
            List<SubscriptionRow> subscriptions;
            using (var db = SelfHostedDb.Create()) subscriptions = await db.Subscriptions.AsNoTracking().ToListAsync();
            foreach (var target in subscriptions.Select(x => new { x.ContentType, x.TmdbId }).Distinct())
            {
                string resource = target.ContentType == "person" ? $"person/{target.TmdbId}/combined_credits" : $"{target.ContentType}/{target.TmdbId}";
                string url = $"{conf.public_url.TrimEnd('/')}/tmdb/api/3/{resource}?api_key=4ef0d7355d9ffb5151e987764708ce96&language=ru-RU";
                string json;
                try { json = await FeedHttp.GetStringAsync(url); }
                catch { continue; }
                string hash = SelfHostedSecurity.TokenHash(json);
                using var db = SelfHostedDb.Create();
                var subscribers = subscriptions.Where(x => x.ContentType == target.ContentType && x.TmdbId == target.TmdbId).Select(x => x.UserId).ToList();
                foreach (string userId in subscribers)
                {
                    string key = $"{target.ContentType}:{target.TmdbId}";
                    var state = await db.SyncItems.FirstOrDefaultAsync(x => x.UserId == userId && x.Kind == "feed_state" && x.ItemKey == key);
                    if (state == null)
                        db.SyncItems.Add(new SyncItemRow { UserId = userId, Kind = "feed_state", ItemKey = key, Data = hash, Revision = 0 });
                    else if (state.Data != hash)
                    {
                        state.Data = hash; state.UpdatedAt = DateTime.UtcNow;
                        db.Notifications.Add(new NotificationRow { UserId = userId, Data = System.Text.Json.JsonSerializer.Serialize(new { type = target.ContentType, tmdbId = target.TmdbId, message = "В подписке появились изменения" }) });
                    }
                }
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) { Console.WriteLine($"SelfHosted feed: {ex.Message}"); }
        finally { FeedLock.Release(); }
    }

    public void Dispose()
    {
        maintenanceTimer?.Dispose();
        EventListener.UpdateInitFile -= Reload;
        EventListener.MiddlewareAsync -= IdentityMiddleware;
    }
}
