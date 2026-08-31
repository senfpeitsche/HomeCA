using HomeCA.Service.Security;

namespace HomeCA.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void PasswordHasher_RoundTrips()
    {
        var password = "MySecurePassword123!";
        var hash = PasswordHasher.Hash(password);

        Assert.True(PasswordHasher.Verify(password, hash));
    }

    [Fact]
    public void PasswordHasher_Rejects_Wrong_Password()
    {
        var hash = PasswordHasher.Hash("CorrectPassword123!");

        Assert.False(PasswordHasher.Verify("WrongPassword123!", hash));
    }

    [Fact]
    public void PasswordHasher_Generates_Unique_Hashes()
    {
        var password = "SamePassword123!";
        var hash1 = PasswordHasher.Hash(password);
        var hash2 = PasswordHasher.Hash(password);

        Assert.NotEqual(hash1, hash2); // Different salts
        Assert.True(PasswordHasher.Verify(password, hash1));
        Assert.True(PasswordHasher.Verify(password, hash2));
    }

    [Fact]
    public void LoginRateLimiter_Allows_Initial_Attempts()
    {
        var limiter = new LoginRateLimiter();

        Assert.False(limiter.IsBlocked("192.168.1.1"));
    }

    [Fact]
    public void LoginRateLimiter_Blocks_After_Five_Failures()
    {
        var limiter = new LoginRateLimiter();
        var ip = "10.0.0.1";

        for (var i = 0; i < 5; i++) limiter.RecordFailure(ip);

        Assert.True(limiter.IsBlocked(ip));
    }

    [Fact]
    public void LoginRateLimiter_Does_Not_Block_Other_IPs()
    {
        var limiter = new LoginRateLimiter();

        for (var i = 0; i < 5; i++) limiter.RecordFailure("10.0.0.1");

        Assert.False(limiter.IsBlocked("10.0.0.2"));
    }

    [Fact]
    public void LoginRateLimiter_Clears_On_Success()
    {
        var limiter = new LoginRateLimiter();
        var ip = "10.0.0.3";

        for (var i = 0; i < 4; i++) limiter.RecordFailure(ip);
        limiter.RecordSuccess(ip);

        Assert.False(limiter.IsBlocked(ip));
    }
}
