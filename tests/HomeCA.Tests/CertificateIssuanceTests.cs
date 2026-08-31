using HomeCA.Service.Pki;
using HomeCA.Service.Deployments;
using HomeCA.Service.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class CertificateIssuanceTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    private async Task<(CertificateAuthorityService Authorities, CertificateIssuanceService Certificates)> SetupAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var profiles = new TargetProfileRegistry(storage);
        var deployments = new DeploymentPackageService(profiles);
        var certificates = new CertificateIssuanceService(storage, deployments, authorities, _fixture.CreateOptions());
        return (authorities, certificates);
    }

    [Fact]
    public async Task Issue_ECC_Certificate_With_DnsName()
    {
        var (_, certificates) = await SetupAsync();
        var request = new IssueCertificateRequest("TLS", ["test.example.com"], [], 365, "ECC");

        var result = await certificates.IssueAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("test.example.com", result.Subject);
        Assert.Equal("ECC", result.KeyAlgorithm);
        Assert.True(File.Exists(Path.Combine(result.ExportPath, "certificate.pem")));
        Assert.True(File.Exists(Path.Combine(result.ExportPath, "key.pem")));
        Assert.True(File.Exists(Path.Combine(result.ExportPath, "chain.pem")));
        Assert.True(File.Exists(Path.Combine(result.ExportPath, "fullchain.pem")));
        Assert.True(File.Exists(Path.Combine(result.ExportPath, "bundle.pem")));
    }

    [Fact]
    public async Task Issue_RSA_Certificate()
    {
        var (_, certificates) = await SetupAsync();
        var request = new IssueCertificateRequest("TLS", ["rsa.example.com"], [], 365, "RSA", 2048);

        var result = await certificates.IssueAsync(request, CancellationToken.None);

        Assert.Equal("RSA", result.KeyAlgorithm);
    }

    [Fact]
    public async Task Issue_Certificate_With_IP_SAN()
    {
        var (_, certificates) = await SetupAsync();
        var request = new IssueCertificateRequest("TLS", ["server.example.com"], ["192.168.1.100"]);

        var result = await certificates.IssueAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        var details = await certificates.GetDetailsAsync(result.Id, CancellationToken.None);
        Assert.NotNull(details);
        Assert.Contains("192.168.1.100", details.IpAddresses);
    }

    [Fact]
    public async Task Issue_MTLS_Certificate()
    {
        var (_, certificates) = await SetupAsync();
        var request = new IssueCertificateRequest("mTLS", ["client.example.com"], []);

        var result = await certificates.IssueAsync(request, CancellationToken.None);

        Assert.Equal("mTLS", result.Usage);
    }

    [Fact]
    public async Task Issue_Rejects_Empty_SANs()
    {
        var (_, certificates) = await SetupAsync();
        var request = new IssueCertificateRequest("TLS", [], []);

        await Assert.ThrowsAsync<ArgumentException>(() => certificates.IssueAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Issue_Rejects_Excessive_Validity()
    {
        var (_, certificates) = await SetupAsync();
        var request = new IssueCertificateRequest("TLS", ["test.example.com"], [], 800);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => certificates.IssueAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task List_Returns_Issued_Certificates()
    {
        var (_, certificates) = await SetupAsync();
        await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["a.example.com"], []), CancellationToken.None);
        await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["b.example.com"], []), CancellationToken.None);

        var list = await certificates.ListAsync(CancellationToken.None);

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Search_Filters_By_Subject()
    {
        var (_, certificates) = await SetupAsync();
        await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["alpha.example.com"], []), CancellationToken.None);
        await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["beta.other.com"], []), CancellationToken.None);

        var results = await certificates.ListAsync(CancellationToken.None, search: "alpha");

        Assert.Single(results);
        Assert.Contains("alpha", results[0].Subject);
    }

    [Fact]
    public async Task GetDetails_Returns_Full_Metadata()
    {
        var (_, certificates) = await SetupAsync();
        var issued = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["detail.example.com"], ["10.0.0.1"]), CancellationToken.None);

        var details = await certificates.GetDetailsAsync(issued.Id, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Contains("detail.example.com", details.DnsNames);
        Assert.Contains("10.0.0.1", details.IpAddresses);
        Assert.Equal("ECC", details.KeyAlgorithm);
        Assert.NotEmpty(details.Sha256Fingerprint);
        Assert.NotEmpty(details.SerialNumber);
        Assert.Equal("TLS", details.Usage);
    }

    [Fact]
    public async Task GetDetails_Returns_Null_For_Unknown_Id()
    {
        var (_, certificates) = await SetupAsync();

        var details = await certificates.GetDetailsAsync("nonexistent", CancellationToken.None);

        Assert.Null(details);
    }

    [Fact]
    public async Task Bundle_Contains_Key_Cert_And_Chain()
    {
        var (_, certificates) = await SetupAsync();
        var result = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["bundle.example.com"], []), CancellationToken.None);

        var bundle = File.ReadAllText(Path.Combine(result.ExportPath, "bundle.pem"));

        Assert.Contains("BEGIN PRIVATE KEY", bundle);
        Assert.Contains("BEGIN CERTIFICATE", bundle);
        // Should contain at least 3 certificates (leaf + issuing + root) plus the key
        var certCount = bundle.Split("BEGIN CERTIFICATE").Length - 1;
        Assert.True(certCount >= 3, $"Expected at least 3 certificates in bundle, found {certCount}");
    }

    public void Dispose() => _fixture.Dispose();
}
