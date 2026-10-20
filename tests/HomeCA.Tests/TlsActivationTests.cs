using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HomeCA.Service.Deployments;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeCA.Tests;

public sealed class TlsActivationTests : IDisposable
{
    private const string HttpAndHttps = "http://0.0.0.0:5080;https://0.0.0.0:5443";
    private readonly TestFixture _fixture = new();
    private readonly ConcurrentQueue<(string Arguments, string? Stdin)> _commands = new();

    [Fact]
    public void Systemd_Override_Keeps_Http_Listener_And_Crl_Base_Url()
    {
        var content = TlsActivationService.BuildSystemdOverride(new TlsConfiguration(
            "https://0.0.0.0:5443", "/etc/homeca/tls/homeca-tls.pfx", "https://ca.example.test:5443", "ca.example.test",
            "http://0.0.0.0:5080", "http://ca.example.test:5080"));

        Assert.Contains($"Environment=ASPNETCORE_URLS={HttpAndHttps}\n", content);
        Assert.Contains("Environment=Storage__CrlBaseUrl=http://ca.example.test:5080\n", content);
        Assert.Contains("Environment=ASPNETCORE_Kestrel__Certificates__Default__Path=/etc/homeca/tls/homeca-tls.pfx\n", content);
    }

    [Theory]
    [InlineData(HttpAndHttps, true, 5443)]
    [InlineData("https://0.0.0.0:5443", false, 0)]
    [InlineData("http://0.0.0.0:5080", false, 0)]
    [InlineData("http://*:5080;https://*:8443", true, 8443)]
    [InlineData(null, false, 0)]
    public void Https_Redirect_Is_Only_Enabled_With_Both_Listeners(string? urls, bool expected, int expectedPort)
    {
        Assert.Equal(expected, PlainHttpPolicy.TryGetHttpsPort(urls, out var port));
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("/api/v1/crl/abc", true)]
    [InlineData("/api/v1/trust-anchor/pem", true)]
    [InlineData("/acme/directory", true)]
    [InlineData("/health", true)]
    [InlineData("/api/v1/crlx", false)]
    [InlineData("/api/v1/login", false)]
    [InlineData("/", false)]
    public void Only_Public_Distribution_Paths_Stay_On_Plain_Http(string path, bool allowed) =>
        Assert.Equal(allowed, PlainHttpPolicy.IsAllowedOverPlainHttp(new PathString(path)));

    [Fact]
    public async Task Crl_Distribution_Point_Uses_Http_Base_Url_When_Public_Url_Is_Https()
    {
        var options = Options.Create(new HomeCaStorageOptions
        {
            RootPath = _fixture.RootPath, BackupPath = _fixture.BackupPath, BackupKeyPath = _fixture.BackupKeyPath,
            CaKeyPath = _fixture.CaKeyPath, ConfigurationPath = _fixture.ConfigurationPath,
            PublicUrl = "https://ca.example.test:5443", CrlBaseUrl = "http://ca.example.test:5080/"
        });
        var certificates = await CreateIssuanceAsync(options);

        var issued = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["host.example.test"], [], 90), CancellationToken.None);

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_fixture.RootPath, "certificates", issued.Id, "certificate.pfx"), null);
        var cdp = Encoding.ASCII.GetString(certificate.Extensions["2.5.29.31"]!.RawData);
        Assert.Contains("http://ca.example.test:5080/api/v1/crl/", cdp);
        Assert.DoesNotContain("https://", cdp);
    }

    [Fact]
    public async Task Renewed_HomeCA_Certificate_Is_Installed_And_Activated()
    {
        var certificates = await CreateIssuanceAsync(_fixture.CreateOptions());
        var setupState = new SetupStateService(_fixture.CreateStorage(), NullLogger<SetupStateService>.Instance);
        var tls = CreateTlsService(setupState, HttpAndHttps);
        var original = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["ca.example.test"], [], 90), CancellationToken.None);
        await setupState.SetHostnameAsync("ca.example.test", CancellationToken.None);
        await setupState.SetTlsCertificateIdAsync(original.Id, CancellationToken.None);
        await tls.InstallCertificateAsync(original.Id, "ca.example.test", CancellationToken.None);
        var renewed = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["ca.example.test"], [], 90), CancellationToken.None);

        Assert.True(await tls.OnCertificateRenewedAsync(original.Id, renewed.Id, CancellationToken.None));

        Assert.Equal(renewed.Id, setupState.Current.TlsCertificateId);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_fixture.RootPath, "certificates", renewed.Id, "certificate.pfx")), File.ReadAllBytes(tls.PfxPath));
        var configuration = await tls.ReadConfigurationAsync(CancellationToken.None);
        Assert.Equal(tls.PfxPath, configuration!.PfxPath);
        Assert.Contains(_commands, command => command.Arguments.StartsWith("tee ") && command.Stdin!.Contains(tls.PfxPath));
        Assert.Contains(_commands, command => command.Arguments == "systemctl daemon-reload");
        Assert.True(await WaitForCommandAsync("systemctl restart homeca"), "The service must be restarted to serve the renewed certificate.");
    }

    [Fact]
    public async Task Renewing_Another_Certificate_Leaves_Tls_Untouched()
    {
        var certificates = await CreateIssuanceAsync(_fixture.CreateOptions());
        var setupState = new SetupStateService(_fixture.CreateStorage(), NullLogger<SetupStateService>.Instance);
        var tls = CreateTlsService(setupState, HttpAndHttps);
        var tlsCertificate = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["ca.example.test"], [], 90), CancellationToken.None);
        await setupState.SetTlsCertificateIdAsync(tlsCertificate.Id, CancellationToken.None);

        Assert.False(await tls.OnCertificateRenewedAsync("some-other-certificate", "its-replacement", CancellationToken.None));

        Assert.Equal(tlsCertificate.Id, setupState.Current.TlsCertificateId);
        Assert.Empty(_commands);
    }

    private TlsActivationService CreateTlsService(SetupStateService setupState, string urls) => new(
        _fixture.CreateStorage(), setupState, NullLogger<TlsActivationService>.Instance,
        (_, arguments, stdin) => { _commands.Enqueue((arguments, stdin)); return new CommandResult(true, "", ""); },
        () => urls);

    private async Task<bool> WaitForCommandAsync(string arguments)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (_commands.Any(command => command.Arguments == arguments)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private async Task<CertificateIssuanceService> CreateIssuanceAsync(IOptions<HomeCaStorageOptions> options)
    {
        var storage = new HomeCaStorage(options, NullLogger<HomeCaStorage>.Instance);
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var deployments = new DeploymentPackageService(new TargetProfileRegistry(storage), NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        return new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, options, NullLogger<CertificateIssuanceService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();
}
