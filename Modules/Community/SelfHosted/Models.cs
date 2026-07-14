using Shared.Models.AppConf;
using Shared.Models.Module;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SelfHosted;

public sealed class SelfHostedConf : ModuleBaseConf
{
    public bool enable { get; set; } = true;
    public string public_url { get; set; } = "";
    public string admin_email { get; set; } = "";
    public string admin_bootstrap_secret { get; set; } = "";
    public int password_iterations { get; set; } = 600_000;
    public int session_idle_days { get; set; } = 30;
    public int session_absolute_days { get; set; } = 180;
    public int pairing_minutes { get; set; } = 5;
    public int backup_max_mb { get; set; } = 10;
}

[Table("users")]
public sealed class UserRow
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [Required] public string Email { get; set; } = "";
    [Required] public string EmailNormalized { get; set; } = "";
    [Required] public string Nickname { get; set; } = "";
    [Required] public string NicknameNormalized { get; set; } = "";
    [Required] public string PasswordHash { get; set; } = "";
    [Required] public string Role { get; set; } = "user";
    [Required] public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public DateTime? DeleteAfter { get; set; }
}

[Table("sessions")]
public sealed class SessionRow
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [Required] public string UserId { get; set; } = "";
    [Required] public string TokenHash { get; set; } = "";
    [Required] public string CsrfToken { get; set; } = "";
    public string DeviceName { get; set; } = "Browser";
    public string UserAgent { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime AbsoluteExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

[Table("pairings")]
public sealed class PairingRow
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [Required] public string TvSecretHash { get; set; } = "";
    [Required] public string ClaimSecretHash { get; set; } = "";
    [Required] public string ShortCode { get; set; } = "";
    public string DeviceName { get; set; } = "TV";
    public string? UserId { get; set; }
    public string? SessionToken { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

[Table("sync_items")]
public sealed class SyncItemRow
{
    [Key] public long Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required] public string Kind { get; set; } = "";
    [Required] public string ItemKey { get; set; } = "";
    [Required] public string Data { get; set; } = "{}";
    public long Revision { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("reactions")]
public sealed class ReactionRow
{
    [Key] public long Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required] public string ContentType { get; set; } = "movie";
    public long TmdbId { get; set; }
    public int Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("subscriptions")]
public sealed class SubscriptionRow
{
    [Key] public long Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required] public string ContentType { get; set; } = "movie";
    public long TmdbId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("notifications")]
public sealed class NotificationRow
{
    [Key] public long Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required] public string Data { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

[Table("plugin_catalog")]
public sealed class PluginRow
{
    [Key] public long Id { get; set; }
    [Required] public string Name { get; set; } = "";
    [Required] public string Url { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Compatibility { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string VerificationStatus { get; set; } = "pending";
    public bool Enabled { get; set; } = true;
    public bool Blacklisted { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("plugin_ratings")]
public sealed class PluginRatingRow
{
    [Key] public long Id { get; set; }
    public long PluginId { get; set; }
    [Required] public string UserId { get; set; } = "";
    public int Value { get; set; }
}

[Table("plugin_installs")]
public sealed class PluginInstallRow
{
    [Key] public long Id { get; set; }
    public long PluginId { get; set; }
    [Required] public string UserId { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}

[Table("password_reset_tokens")]
public sealed class PasswordResetRow
{
    [Key] public long Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required] public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

[Table("audit_log")]
public sealed class AuditRow
{
    [Key] public long Id { get; set; }
    public string? UserId { get; set; }
    [Required] public string Action { get; set; } = "";
    public string Data { get; set; } = "{}";
    public string Ip { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("schema_versions")]
public sealed class SchemaVersionRow
{
    [Key] public int Version { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}

[Table("mutations")]
public sealed class MutationRow
{
    [Key] public long Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required] public string IdempotencyKey { get; set; } = "";
    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record RegisterRequest(string Email, string Nickname, string Password, string? DeviceName);
public sealed record LoginRequest(string Email, string Password, string? DeviceName);
public sealed record PairingRequest(string? DeviceName);
public sealed record ClaimRequest(string? ClaimSecret, string? Code = null);
public sealed record PasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ResetPasswordRequest(string Token, string NewPassword);
public sealed record RenameSessionRequest(string DeviceName);
public sealed record SyncPutRequest(string Kind, string Key, object? Data, string? IdempotencyKey);
public sealed record ReactionRequest(string Type, long TmdbId, int Value);
public sealed record SubscriptionRequest(string Type, long TmdbId, bool Enabled);
public sealed record PluginRatingRequest(int Value);
public sealed record AdminUserRequest(string Action);
