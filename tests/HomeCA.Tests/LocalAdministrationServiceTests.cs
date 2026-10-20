using HomeCA.Service.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class LocalAdministrationServiceTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Default_Admin_Session_Is_Restricted_Until_Password_Changes()
    {
        var service = CreateService();
        await service.EnsureDefaultAdministratorAsync(CancellationToken.None);

        var login = await service.LoginAsync(new LoginRequest("admin", "admin"), CancellationToken.None);
        Assert.NotNull(login);
        Assert.True(login.MustChangePassword);
        var beforeChange = await service.ValidateSessionAsync(login.AccessToken, CancellationToken.None);
        Assert.True(beforeChange.IsValid);
        Assert.True(beforeChange.MustChangePassword);

        Assert.True(await service.ChangePasswordAsync(login.AccessToken, new ChangePasswordRequest("admin", "a-secure-new-password"), CancellationToken.None));
        var afterChange = await service.ValidateSessionAsync(login.AccessToken, CancellationToken.None);
        Assert.True(afterChange.IsValid);
        Assert.False(afterChange.MustChangePassword);
    }

    [Fact]
    public async Task Password_Change_Ends_All_Other_Sessions()
    {
        var service = CreateService();
        await service.EnsureDefaultAdministratorAsync(CancellationToken.None);
        var current = await service.LoginAsync(new LoginRequest("admin", "admin"), CancellationToken.None);
        var other = await service.LoginAsync(new LoginRequest("admin", "admin"), CancellationToken.None);

        Assert.True(await service.ChangePasswordAsync(current!.AccessToken, new ChangePasswordRequest("admin", "a-secure-new-password"), CancellationToken.None));

        Assert.True((await service.ValidateSessionAsync(current.AccessToken, CancellationToken.None)).IsValid);
        Assert.False((await service.ValidateSessionAsync(other!.AccessToken, CancellationToken.None)).IsValid);
        Assert.Null(await service.LoginAsync(new LoginRequest("admin", "admin"), CancellationToken.None));
        // A restarted service reads the same state from disk.
        var restarted = CreateService();
        Assert.True((await restarted.ValidateSessionAsync(current.AccessToken, CancellationToken.None)).IsValid);
        Assert.False((await restarted.ValidateSessionAsync(other.AccessToken, CancellationToken.None)).IsValid);
        Assert.NotNull(await restarted.LoginAsync(new LoginRequest("admin", "a-secure-new-password"), CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_User_Name_Is_Rejected()
    {
        var service = CreateService();
        await service.EnsureDefaultAdministratorAsync(CancellationToken.None);

        Assert.Null(await service.LoginAsync(new LoginRequest("root", "admin"), CancellationToken.None));
    }

    [Fact]
    public async Task Audit_Log_Returns_Written_Events_And_Filters_By_Action()
    {
        var service = CreateService();
        await service.EnsureDefaultAdministratorAsync(CancellationToken.None);
        Assert.Null(await service.LoginAsync(new LoginRequest("admin", "wrong"), CancellationToken.None));
        Assert.NotNull(await service.LoginAsync(new LoginRequest("admin", "admin"), CancellationToken.None));

        var all = await service.ReadAuditLogAsync();
        var failed = await service.ReadAuditLogAsync(action: "login_failed");

        Assert.Equal(3, all.Count);
        Assert.All(all, entry => Assert.Equal("admin", entry.Subject));
        Assert.All(all, entry => Assert.True(entry.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5)));
        Assert.Equal("administrator.login", all[0].Action);
        var entry = Assert.Single(failed);
        Assert.Equal("administrator.login_failed", entry.Action);
    }

    private LocalAdministrationService CreateService() => new(
        _fixture.CreateStorage(),
        new TestHostEnvironment(),
        NullLogger<LocalAdministrationService>.Instance);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "HomeCA.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public void Dispose() => _fixture.Dispose();
}
