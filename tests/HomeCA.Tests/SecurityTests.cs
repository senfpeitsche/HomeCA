using HomeCA.Service.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task LoginRateLimiter_Counts_Parallel_Attempts_Before_They_Finish()
    {
        var limiter = new LoginRateLimiter();
        var ip = "10.0.0.4";

        // All attempts reserve their slot before any of them reports a failure.
        var reserved = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => limiter.TryBeginAttempt(ip))));
        Assert.Equal(5, reserved.Count(allowed => allowed));

        for (var i = 0; i < 5; i++) limiter.CompleteAttempt(ip, succeeded: false);
        Assert.True(limiter.IsBlocked(ip));
        Assert.False(limiter.TryBeginAttempt(ip));
    }

    [Fact]
    public void LoginRateLimiter_Aborted_Attempt_Neither_Counts_Nor_Blocks()
    {
        var limiter = new LoginRateLimiter();
        var ip = "10.0.0.5";

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryBeginAttempt(ip));
            limiter.CompleteAttempt(ip, succeeded: null);
        }

        Assert.False(limiter.IsBlocked(ip));
    }

    [Fact]
    public void LoginRateLimiter_Sweep_Drops_Idle_Addresses()
    {
        var limiter = new LoginRateLimiter();
        Assert.True(limiter.TryBeginAttempt("10.0.0.6"));
        limiter.CompleteAttempt("10.0.0.6", succeeded: true);
        limiter.RecordFailure("10.0.0.7");

        limiter.Sweep();

        Assert.Equal(1, limiter.TrackedAddressCount);
    }

    [Fact]
    public async Task ValidateSession_Rejects_Malformed_BearerToken_Without_Throwing()
    {
        using var fixture = new TestFixture();
        var service = new LocalAdministrationService(
            fixture.CreateStorage(), new TestHostEnvironment(), NullLogger<LocalAdministrationService>.Instance);

        var session = await service.ValidateSessionAsync("xyz!", CancellationToken.None);

        Assert.False(session.IsValid);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "HomeCA.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
