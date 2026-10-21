using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HomeCA.Service.Acme;
using HomeCA.Service.Automation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Endpoints;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class DeploymentTargetTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Token_is_stored_hashed_and_only_the_right_token_authenticates()
    {
        var registry = CreateRegistry();

        var created = await registry.CreateAsync(new SaveDeploymentTargetRequest("pve01", "abcdef01", "pve01.lab.test"), CancellationToken.None);

        var stored = await File.ReadAllTextAsync(Path.Combine(_fixture.RootPath, "state", "deployment-targets.json"));
        Assert.DoesNotContain(created.Token, stored);
        Assert.Contains(DeploymentTargetRegistry.HashToken(created.Token), stored);
        Assert.NotNull(await registry.AuthenticateAsync(created.Target.Id, created.Token, CancellationToken.None));
        Assert.Null(await registry.AuthenticateAsync(created.Target.Id, created.Token + "x", CancellationToken.None));
        Assert.Null(await registry.AuthenticateAsync(created.Target.Id, null, CancellationToken.None));
        Assert.Null(await registry.AuthenticateAsync("other", created.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Rotating_the_token_invalidates_the_old_one()
    {
        var registry = CreateRegistry();
        var created = await registry.CreateAsync(new SaveDeploymentTargetRequest("nas", "abcdef02"), CancellationToken.None);

        var rotated = await registry.RotateTokenAsync(created.Target.Id, CancellationToken.None);

        Assert.NotNull(rotated);
        Assert.Null(await registry.AuthenticateAsync(created.Target.Id, created.Token, CancellationToken.None));
        Assert.NotNull(await registry.AuthenticateAsync(created.Target.Id, rotated, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "abcdef", null, 443, null)]
    [InlineData("name", "not-hex", null, 443, null)]
    [InlineData("name", "abcdef", "bad host!", 443, null)]
    [InlineData("name", "abcdef", null, 0, null)]
    [InlineData("name", "abcdef", null, 443, "ftp://example.test/hook")]
    public async Task Invalid_targets_are_rejected(string name, string certificateId, string? host, int port, string? webhook) =>
        await Assert.ThrowsAsync<ArgumentException>(() => CreateRegistry().CreateAsync(new SaveDeploymentTargetRequest(name, certificateId, host, port, webhook), CancellationToken.None));

    [Fact]
    public async Task Renewal_moves_plans_and_targets_and_calls_the_webhook()
    {
        var (certificates, renewals, plans, targets, handler) = await SetupRenewalAsync();
        var original = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["pve01.lab.test"], [], 90, TargetProfileId: "proxmox"), CancellationToken.None);
        await plans.AddAsync(new CreateRenewalPlanRequest(original.Id, 30), CancellationToken.None);
        var target = (await targets.CreateAsync(new SaveDeploymentTargetRequest("pve01", original.Id, WebhookUrl: "http://hooks.lab.test/renewed"), CancellationToken.None)).Target;

        var renewed = await renewals.RenewAsync(original.Id, CancellationToken.None);

        Assert.NotEqual(original.Id, renewed.Id);
        Assert.Equal(renewed.Id, Assert.Single(await plans.ListAsync(CancellationToken.None)).CertificateId);
        Assert.Equal(renewed.Id, (await targets.GetAsync(target.Id, CancellationToken.None))!.CertificateId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://hooks.lab.test/renewed", request.Uri);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("certificate.renewed", payload.RootElement.GetProperty("event").GetString());
        Assert.Equal(renewed.Id, payload.RootElement.GetProperty("certificateId").GetString());
        Assert.True(File.Exists(Path.Combine(renewed.ExportPath, "install.sh")));
    }

    [Fact]
    public async Task Certificates_without_a_HomeCA_key_cannot_be_renewed()
    {
        var (certificates, renewals, _, _, _) = await SetupRenewalAsync();
        var issued = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["acme.lab.test"], []), CancellationToken.None);
        var pfxPath = Path.Combine(_fixture.RootPath, "certificates", issued.Id, "certificate.pfx");
        using (var withKey = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null))
            await File.WriteAllBytesAsync(pfxPath, X509CertificateLoader.LoadCertificate(withKey.RawData).Export(X509ContentType.Pkcs12));

        await Assert.ThrowsAsync<InvalidOperationException>(() => renewals.RenewAsync(issued.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("aa", "aa", "current")]
    [InlineData("bb", "aa", "outdated")]
    [InlineData("cc", "aa", "foreign")]
    public void Served_certificates_are_classified(string served, string current, string expected) =>
        Assert.Equal(expected, DeploymentProbeService.Classify(served, current, new HashSet<string> { "aa", "bb" }));

    [Fact]
    public async Task Probe_recognizes_the_current_certificate_on_a_live_tls_endpoint()
    {
        var (certificates, _, _, targets, _) = await SetupRenewalAsync();
        var issued = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["localhost"], ["127.0.0.1"]), CancellationToken.None);
        using var serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_fixture.RootPath, "certificates", issued.Id, "certificate.pfx"), null);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(serverCertificate);
        });
        var target = (await targets.CreateAsync(new SaveDeploymentTargetRequest("local", issued.Id, "127.0.0.1", port), CancellationToken.None)).Target;
        var probes = new DeploymentProbeService(_fixture.CreateStorage(), targets, NullLogger<DeploymentProbeService>.Instance);

        var result = await probes.ProbeAsync(target.Id, CancellationToken.None);

        await server;
        listener.Stop();
        Assert.Equal(DeploymentProbeResult.Current, result!.Status);
        Assert.Equal(DeploymentProbeResult.Current, (await targets.GetAsync(target.Id, CancellationToken.None))!.LastProbe!.Status);
    }

    [Fact]
    public async Task Probe_reports_an_unreachable_host()
    {
        var targets = CreateRegistry();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var target = (await targets.CreateAsync(new SaveDeploymentTargetRequest("gone", "abcdef03", "127.0.0.1", port), CancellationToken.None)).Target;

        var result = await new DeploymentProbeService(_fixture.CreateStorage(), targets, NullLogger<DeploymentProbeService>.Instance).ProbeAsync(target.Id, CancellationToken.None);

        Assert.Equal(DeploymentProbeResult.Unreachable, result!.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Agent_script_contains_no_token_and_installs_atomically()
    {
        var target = new DeploymentTarget("0123456789abcdef", "pve \"01\"", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);

        var script = DeploymentAgentScript.Render("https://ca.lab.test:5443/", target);
        var install = DeploymentAgentScript.RenderInstallCommands("https://ca.lab.test:5443", target, "secret-token");

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("HOMECA_URL=\"https://ca.lab.test:5443\"", script);
        Assert.Contains("mv \"$incoming\" \"$STATE_DIR/current\"", script);
        Assert.DoesNotContain("secret-token", script);
        Assert.DoesNotContain("\r", script);
        Assert.DoesNotContain("\"01\"", script);
        Assert.Contains("secret-token", install);
        Assert.Contains("/etc/cron.d/homeca-agent-01234567", install);
    }

    [Fact]
    public async Task Agent_only_sees_certificate_files_and_the_profile_script()
    {
        var (certificates, _, _, _, _) = await SetupRenewalAsync();
        var issued = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["nginx.lab.test"], [], TargetProfileId: "nginx"), CancellationToken.None);

        var files = DeploymentEndpoints.AvailableFiles(issued.ExportPath).ToList();

        Assert.Equal(["certificate.pem", "key.pem", "chain.pem", "fullchain.pem", "bundle.pem", "install.sh"], files);
    }

    private DeploymentTargetRegistry CreateRegistry() => new(_fixture.CreateStorage(), NullLogger<DeploymentTargetRegistry>.Instance);

    private async Task<(CertificateIssuanceService Certificates, CertificateRenewalService Renewals, RenewalPlanRegistry Plans, DeploymentTargetRegistry Targets, RecordingHandler Handler)> SetupRenewalAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var deployments = new DeploymentPackageService(new TargetProfileRegistry(storage), NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        var certificates = new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
        var plans = new RenewalPlanRegistry(storage, NullLogger<RenewalPlanRegistry>.Instance);
        var targets = CreateRegistry();
        var probes = new DeploymentProbeService(storage, targets, NullLogger<DeploymentProbeService>.Instance);
        var handler = new RecordingHandler();
        var webhooks = new DeploymentWebhookService(new SingleClientFactory(handler), storage, probes, NullLogger<DeploymentWebhookService>.Instance);
        var tls = new TlsActivationService(storage, new SetupStateService(storage, NullLogger<SetupStateService>.Instance), NullLogger<TlsActivationService>.Instance);
        var renewals = new CertificateRenewalService(storage, certificates, plans, targets, webhooks, tls, NullLogger<CertificateRenewalService>.Instance);
        return (certificates, renewals, plans, targets, handler);
    }

    public void Dispose() => _fixture.Dispose();

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    internal sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
