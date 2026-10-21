using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Acme;
using HomeCA.Service.Automation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class CsrSigningTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Csr_is_signed_with_requested_sans_and_without_a_private_key_export()
    {
        var certificates = await SetupAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = CreateCsr("CN=idrac", key, ["idrac.lab.test"], ["10.0.0.20"], requestCa: true);

        var result = await certificates.SignCsrAsync(new SignCsrRequest(csr, ValidityDays: 90), CancellationToken.None);

        using var issued = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(result.ExportPath, "certificate.pem"));
        var san = issued.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(["idrac.lab.test"], san.EnumerateDnsNames());
        Assert.Equal("10.0.0.20", san.EnumerateIPAddresses().Single().ToString());
        Assert.False(issued.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), issued.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.False(File.Exists(Path.Combine(result.ExportPath, "key.pem")));
        Assert.False(File.Exists(Path.Combine(result.ExportPath, "bundle.pem")));
        Assert.True(File.Exists(Path.Combine(result.ExportPath, "request.csr")));
        Assert.True(File.Exists(Path.Combine(result.ExportPath, "fullchain.pem")));
        Assert.Contains(await certificates.ListAsync(CancellationToken.None), item => item.Id == result.Id);
        Assert.Null(RenewalBackgroundService.BuildRenewalRequest(_fixture.CreateStorage(), result.Id));
    }

    [Fact]
    public async Task Explicit_names_replace_the_requested_ones_and_cn_is_a_fallback()
    {
        var certificates = await SetupAsync();
        using var rsa = RSA.Create(2048);

        var cnOnly = await certificates.SignCsrAsync(new SignCsrRequest(CreateCsr("CN=switch01.lab.test", rsa, [], [])), CancellationToken.None);
        var overridden = await certificates.SignCsrAsync(new SignCsrRequest(CreateCsr("CN=x", rsa, ["ignored.lab.test"], []), DnsNames: ["chosen.lab.test"]), CancellationToken.None);

        Assert.Equal("CN=switch01.lab.test", cnOnly.Subject);
        Assert.Equal("RSA", cnOnly.KeyAlgorithm);
        Assert.Equal("CN=chosen.lab.test", overridden.Subject);
    }

    [Fact]
    public async Task Weak_or_tampered_requests_are_rejected()
    {
        var certificates = await SetupAsync();
        using var weak = RSA.Create(1024);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = CreateCsr("CN=ok.lab.test", key, ["ok.lab.test"], []);
        var der = Convert.FromBase64String(string.Concat(valid.Split('\n').Where(line => !line.StartsWith("-----"))));
        der[^5] ^= 0xFF; // break the signature
        var tampered = PemEncoding.WriteString("CERTIFICATE REQUEST", der);

        await Assert.ThrowsAsync<ArgumentException>(() => certificates.SignCsrAsync(new SignCsrRequest(CreateCsr("CN=weak.lab.test", weak, ["weak.lab.test"], [])), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => certificates.SignCsrAsync(new SignCsrRequest(tampered), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => certificates.SignCsrAsync(new SignCsrRequest("not a csr"), CancellationToken.None));
    }

    private static string CreateCsr(string subject, AsymmetricAlgorithm key, string[] dnsNames, string[] ipAddresses, bool requestCa = false)
    {
        var request = key is RSA rsa
            ? new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, (ECDsa)key, HashAlgorithmName.SHA256);
        if (dnsNames.Length > 0 || ipAddresses.Length > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames) san.AddDnsName(name);
            foreach (var address in ipAddresses) san.AddIpAddress(System.Net.IPAddress.Parse(address));
            request.CertificateExtensions.Add(san.Build());
        }
        if (requestCa) request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSigningRequestPem();
    }

    private async Task<CertificateIssuanceService> SetupAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var deployments = new DeploymentPackageService(new TargetProfileRegistry(storage), NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        return new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();
}
