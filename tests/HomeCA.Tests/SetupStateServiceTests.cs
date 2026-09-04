using HomeCA.Service.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class SetupStateServiceTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Setup_State_Advances_In_Order_And_Persists_Instance_Details()
    {
        var storage = _fixture.CreateStorage();
        var setup = new SetupStateService(storage, NullLogger<SetupStateService>.Instance);

        await setup.SetHostnameAsync("homeca.example.test", CancellationToken.None);
        await setup.SetTlsCertificateIdAsync("certificate-id", CancellationToken.None);
        await setup.AdvanceAsync(SetupPhase.Initial, CancellationToken.None);
        await setup.AdvanceAsync(SetupPhase.PasswordChanged, CancellationToken.None);
        await setup.AdvanceAsync(SetupPhase.CaInitialized, CancellationToken.None);
        await setup.AdvanceAsync(SetupPhase.TlsConfigured, CancellationToken.None);

        var reloaded = new SetupStateService(storage, NullLogger<SetupStateService>.Instance);
        Assert.True(reloaded.IsSetupComplete);
        Assert.Equal("homeca.example.test", reloaded.Current.Hostname);
        Assert.Equal("certificate-id", reloaded.Current.TlsCertificateId);
    }

    [Fact]
    public async Task Setup_State_Does_Not_Skip_Phases()
    {
        var setup = new SetupStateService(_fixture.CreateStorage(), NullLogger<SetupStateService>.Instance);

        var state = await setup.AdvanceAsync(SetupPhase.CaInitialized, CancellationToken.None);

        Assert.Equal(SetupPhase.Initial, state.SetupPhase);
    }

    public void Dispose() => _fixture.Dispose();
}
