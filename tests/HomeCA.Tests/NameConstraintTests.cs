using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Acme;
using HomeCA.Service.Deployments;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class NameConstraintTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public void Extension_round_trips_domains_and_networks()
    {
        var permitted = NameConstraints.Normalize([".Lab.Example.com", "home.arpa"], ["192.168.0.0/16", "fd00::/8"]);
        using var key = System.Security.Cryptography.ECDsa.Create();
        var request = new CertificateRequest("CN=Constrained", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(NameConstraints.Build(permitted));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var read = NameConstraints.Read(certificate)!;

        Assert.Equal(["lab.example.com", "home.arpa"], read.DnsDomains);
        Assert.Equal(["192.168.0.0/16", "fd00::/8"], read.IpNetworks);
        Assert.True(certificate.Extensions[NameConstraints.Oid]!.Critical);
    }

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("nas.example.com", "example.com", true)]
    [InlineData("*.example.com", "example.com", true)]
    [InlineData("badexample.com", "example.com", false)]
    [InlineData("example.com.evil.test", "example.com", false)]
    public void Dns_names_match_the_domain_and_its_subdomains(string name, string domain, bool expected) =>
        Assert.Equal(expected, NameConstraints.IsDnsPermitted(name, [domain]));

    [Theory]
    [InlineData("example.com/24")]
    [InlineData("*.example.com")]
    public void Invalid_constraints_are_rejected(string value) =>
        Assert.Throws<ArgumentException>(() => NameConstraints.Normalize(value.Contains('/') ? [] : [value], value.Contains('/') ? [value] : []));

    [Fact]
    public void Host_bits_in_a_network_are_cleared() =>
        Assert.Equal(["192.168.0.0/16"], NameConstraints.Normalize([], ["192.168.1.5/16"]).IpNetworks);

    [Fact]
    public async Task Constrained_issuing_ca_refuses_names_outside_its_limits()
    {
        var (authorities, certificates) = await SetupConstrainedAsync();

        var inside = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["nas.lab.example.com"], ["192.168.10.5"]), CancellationToken.None);

        Assert.NotNull(inside);
        await Assert.ThrowsAsync<ArgumentException>(() => certificates.IssueAsync(new IssueCertificateRequest("TLS", ["bank.example.org"], []), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => certificates.IssueAsync(new IssueCertificateRequest("TLS", ["nas.lab.example.com"], ["8.8.8.8"]), CancellationToken.None));
        // No IPv6 network is permitted, so IPv6 stays unconstrained as RFC 5280 defines.
        Assert.NotNull(await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["v6.lab.example.com"], ["2001:db8::1"]), CancellationToken.None));
        var item = (await authorities.ListAsync(CancellationToken.None)).Single(authority => authority.Name == "Constrained CA");
        Assert.Equal(["lab.example.com"], item.PermittedDnsDomains);
    }

    [Fact]
    public async Task Platform_chain_validation_accepts_the_constrained_hierarchy()
    {
        var (authorities, certificates) = await SetupConstrainedAsync();
        var issued = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["nas.lab.example.com"], []), CancellationToken.None);
        using var leaf = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(issued.ExportPath, "certificate.pem"));
        var chainPem = await File.ReadAllTextAsync(Path.Combine(issued.ExportPath, "chain.pem"));
        var chainCertificates = new X509Certificate2Collection();
        chainCertificates.ImportFromPem(chainPem);
        var root = chainCertificates.Single(certificate => certificate.Subject == certificate.Issuer);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.AddRange(chainCertificates);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        Assert.True(chain.Build(leaf), string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation)));
    }

    [Fact]
    public async Task Rotation_keeps_the_limits_of_the_replaced_ca()
    {
        var (authorities, _) = await SetupConstrainedAsync();
        var root = (await authorities.ListAsync(CancellationToken.None)).Single(authority => authority.Type == "root");

        var result = await authorities.RotateIntermediateAsync(new RotateIntermediateRequest("Rotated CA", "CN=Rotated", root.Id, 365, "ECC", 7), CancellationToken.None);

        Assert.Equal(["lab.example.com"], result.ReplacementAuthority.PermittedDnsDomains);
        Assert.Equal(["192.168.0.0/16"], result.ReplacementAuthority.PermittedIpNetworks);
    }

    [Fact]
    public async Task Root_cas_cannot_carry_constraints()
    {
        var authorities = new CertificateAuthorityService(_fixture.CreateStorage(), NullLogger<CertificateAuthorityService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => authorities.CreateAsync(new CreateAuthorityRequest("Root", "CN=Root", "root", null, 3650, "ECC", 7, ["lab.example.com"]), CancellationToken.None));
    }

    private async Task<(CertificateAuthorityService Authorities, CertificateIssuanceService Certificates)> SetupConstrainedAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var root = (await authorities.ListAsync(CancellationToken.None)).Single(authority => authority.Type == "root");
        await authorities.RotateIntermediateAsync(new RotateIntermediateRequest("Constrained CA", "CN=Constrained CA", root.Id, 1000, "ECC", 7, ["lab.example.com"], ["192.168.0.0/16"]), CancellationToken.None);
        var deployments = new DeploymentPackageService(new TargetProfileRegistry(storage), NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        return (authorities, new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance));
    }

    public void Dispose() => _fixture.Dispose();
}
