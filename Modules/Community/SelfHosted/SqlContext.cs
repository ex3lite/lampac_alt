using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SelfHosted;

public sealed class SelfHostedDb : DbContext
{
    public static string DbPath { get; private set; } = Path.Combine("database", "SelfHosted.sql");
    public static IDbContextFactory<SelfHostedDb>? Factory { get; private set; }

    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<PairingRow> Pairings => Set<PairingRow>();
    public DbSet<SyncItemRow> SyncItems => Set<SyncItemRow>();
    public DbSet<ReactionRow> Reactions => Set<ReactionRow>();
    public DbSet<SubscriptionRow> Subscriptions => Set<SubscriptionRow>();
    public DbSet<NotificationRow> Notifications => Set<NotificationRow>();
    public DbSet<PluginRow> Plugins => Set<PluginRow>();
    public DbSet<PluginRatingRow> PluginRatings => Set<PluginRatingRow>();
    public DbSet<PluginInstallRow> PluginInstalls => Set<PluginInstallRow>();
    public DbSet<PasswordResetRow> PasswordResets => Set<PasswordResetRow>();
    public DbSet<AuditRow> Audit => Set<AuditRow>();
    public DbSet<SchemaVersionRow> SchemaVersions => Set<SchemaVersionRow>();
    public DbSet<MutationRow> Mutations => Set<MutationRow>();

    public SelfHostedDb() { }
    public SelfHostedDb(DbContextOptions<SelfHostedDb> options) : base(options) { }

    public static void Configure(DbContextOptionsBuilder options)
    {
        if (!options.IsConfigured)
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = DbPath, Cache = SqliteCacheMode.Shared, DefaultTimeout = 10, Pooling = true }.ToString());
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options) => Configure(options);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserRow>().HasIndex(x => x.EmailNormalized).IsUnique();
        b.Entity<UserRow>().HasIndex(x => x.NicknameNormalized).IsUnique();
        b.Entity<SessionRow>().HasIndex(x => x.TokenHash).IsUnique();
        b.Entity<PairingRow>().HasIndex(x => x.ClaimSecretHash).IsUnique();
        b.Entity<PairingRow>().HasIndex(x => x.ShortCode).IsUnique();
        b.Entity<SyncItemRow>().HasIndex(x => new { x.UserId, x.Kind, x.ItemKey }).IsUnique();
        b.Entity<ReactionRow>().HasIndex(x => new { x.UserId, x.ContentType, x.TmdbId }).IsUnique();
        b.Entity<SubscriptionRow>().HasIndex(x => new { x.UserId, x.ContentType, x.TmdbId }).IsUnique();
        b.Entity<PluginRow>().HasIndex(x => x.Url).IsUnique();
        b.Entity<PluginRatingRow>().HasIndex(x => new { x.PluginId, x.UserId }).IsUnique();
        b.Entity<PluginInstallRow>().HasIndex(x => new { x.PluginId, x.UserId }).IsUnique();
        b.Entity<MutationRow>().HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
    }

    public static SelfHostedDb Create() => Factory?.CreateDbContext() ?? new SelfHostedDb();

    public static void Initialize(IServiceProvider services)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Factory = services.GetService<IDbContextFactory<SelfHostedDb>>();
        using var db = Create();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=10000;");
        if (!db.SchemaVersions.Any())
        {
            db.SchemaVersions.Add(new SchemaVersionRow { Version = 1 });
            db.SaveChanges();
        }
        if (!db.SchemaVersions.Any(x => x.Version == 2))
        {
            using var tx = db.Database.BeginTransaction();
            db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS mutations (Id INTEGER NOT NULL CONSTRAINT PK_mutations PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, IdempotencyKey TEXT NOT NULL, Revision INTEGER NOT NULL, CreatedAt TEXT NOT NULL)");
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_mutations_UserId_IdempotencyKey ON mutations (UserId, IdempotencyKey)");
            db.SchemaVersions.Add(new SchemaVersionRow { Version = 2 });
            db.SaveChanges();
            tx.Commit();
        }
        if (!db.SchemaVersions.Any(x => x.Version == 3))
        {
            using var tx = db.Database.BeginTransaction();
            try { db.Database.ExecuteSqlRaw("ALTER TABLE plugin_catalog ADD COLUMN Compatibility TEXT NOT NULL DEFAULT ''"); } catch (SqliteException) { }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE plugin_catalog ADD COLUMN VerificationStatus TEXT NOT NULL DEFAULT 'pending'"); } catch (SqliteException) { }
            db.SchemaVersions.Add(new SchemaVersionRow { Version = 3 });
            db.SaveChanges();
            tx.Commit();
        }
    }
}
