using SelfHosted;
using SelfHosted.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace SelfHosted.Tests;

public sealed class SecurityTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/lampa-main/index.html", true)]
    [InlineData("/lampa-main/app.min.js", true)]
    [InlineData("/online.js", true)]
    [InlineData("/plugins/custom.js", true)]
    [InlineData("/api/v1/auth/login", false)]
    [InlineData("/account/", false)]
    [InlineData("/account/pair/secret", false)]
    [InlineData("/lampa-main/css/app.css", false)]
    public void Executable_lampa_surface_requires_session(string path, bool expected)
        => Assert.Equal(expected, SelfHostedRuntime.IsProtectedLampaPath(path));

    [Theory]
    [InlineData("/account/", "GET", true)]
    [InlineData("/account/pair/secret", "GET", true)]
    [InlineData("/account/tmdb-logo.svg", "GET", true)]
    [InlineData("/api/v1/auth/register", "POST", true)]
    [InlineData("/api/v1/auth/login", "POST", true)]
    [InlineData("/api/v1/auth/pairings", "POST", true)]
    [InlineData("/api/v1/auth/pairings/id/wait", "GET", true)]
    [InlineData("/cub/red/api/test", "GET", true)]
    [InlineData("/account/admin/", "GET", false)]
    [InlineData("/api/v1/auth/pairings/claim", "POST", false)]
    [InlineData("/timecode", "GET", false)]
    [InlineData("/nws", "GET", false)]
    [InlineData("/tmdb/api/3/movie/1", "GET", false)]
    public void Only_auth_bootstrap_surface_is_public(string path, string method, bool expected)
        => Assert.Equal(expected, SelfHostedRuntime.IsPublicUnauthenticatedPath(path, method));

    [Fact]
    public void PasswordHash_IsVersionedSaltedAndVerifiable()
    {
        const string password = "correct-horse-battery-staple";
        string first = SelfHostedSecurity.HashPassword(password, 10_000);
        string second = SelfHostedSecurity.HashPassword(password, 10_000);

        Assert.StartsWith("pbkdf2-sha256$1$10000$", first);
        Assert.NotEqual(first, second);
        Assert.True(SelfHostedSecurity.VerifyPassword(password, first));
        Assert.False(SelfHostedSecurity.VerifyPassword(password + "!", first));
        Assert.False(SelfHostedSecurity.VerifyPassword(password, "broken"));
    }

    [Fact]
    public void LoginPassword_UsesDummyPbkdf2ForUnknownUser()
    {
        const string password = "correct-horse-battery-staple";
        string hash = SelfHostedSecurity.HashPassword(password, 10_000);

        Assert.True(SelfHostedSecurity.VerifyLoginPassword(password, hash, 10_000));
        Assert.False(SelfHostedSecurity.VerifyLoginPassword(password, null, 10_000));
    }

    [Fact]
    public void PairingClaim_IsRateLimited()
    {
        var method = typeof(SelfHostedController).GetMethod(nameof(SelfHostedController.ClaimPairing))!;
        var limit = Assert.Single(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), false).Cast<EnableRateLimitingAttribute>());

        Assert.Equal("selfhost-pair", limit.PolicyName);
    }

    [Fact]
    public void Tokens_AreRandomAndOnlyTheirHashNeedsStorage()
    {
        string first = SelfHostedSecurity.RandomToken();
        string second = SelfHostedSecurity.RandomToken();

        Assert.NotEqual(first, second);
        Assert.Equal(64, SelfHostedSecurity.TokenHash(first).Length);
        Assert.Equal(SelfHostedSecurity.TokenHash(first), SelfHostedSecurity.TokenHash(first));
    }

    [Fact]
    public void SlidingAttemptWindow_RejectsRequestsOverLimit()
    {
        string key = Guid.NewGuid().ToString("N");

        Assert.True(SelfHostedSecurity.TryConsume("test", key, 2, TimeSpan.FromMinutes(1), out int firstRetry));
        Assert.True(SelfHostedSecurity.TryConsume("test", key, 2, TimeSpan.FromMinutes(1), out int secondRetry));
        Assert.False(SelfHostedSecurity.TryConsume("test", key, 2, TimeSpan.FromMinutes(1), out int rejectedRetry));
        Assert.Equal(0, firstRetry);
        Assert.Equal(0, secondRetry);
        Assert.InRange(rejectedRetry, 1, 60);
    }

    [Fact]
    public void IdentityNormalization_IsDeterministic()
    {
        Assert.Equal("user@example.com", SelfHostedSecurity.NormalizeEmail(" User@Example.COM "));
        Assert.Equal("nickname", SelfHostedSecurity.NormalizeNickname(" NickName "));
        Assert.Equal(120, SelfHostedSecurity.CleanDeviceName(new string('x', 200)).Length);
    }
}
