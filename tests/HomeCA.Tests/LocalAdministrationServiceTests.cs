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
