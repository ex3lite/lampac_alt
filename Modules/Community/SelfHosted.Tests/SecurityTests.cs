using SelfHosted;
using Xunit;

namespace SelfHosted.Tests;

public sealed class SecurityTests
{
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
    public void Tokens_AreRandomAndOnlyTheirHashNeedsStorage()
    {
        string first = SelfHostedSecurity.RandomToken();
        string second = SelfHostedSecurity.RandomToken();

        Assert.NotEqual(first, second);
        Assert.Equal(64, SelfHostedSecurity.TokenHash(first).Length);
        Assert.Equal(SelfHostedSecurity.TokenHash(first), SelfHostedSecurity.TokenHash(first));
    }

    [Fact]
    public void IdentityNormalization_IsDeterministic()
    {
        Assert.Equal("user@example.com", SelfHostedSecurity.NormalizeEmail(" User@Example.COM "));
        Assert.Equal("nickname", SelfHostedSecurity.NormalizeNickname(" NickName "));
        Assert.Equal(120, SelfHostedSecurity.CleanDeviceName(new string('x', 200)).Length);
    }
}
