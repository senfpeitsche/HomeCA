using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Pki;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.X509;

namespace HomeCA.Tests;

public sealed class CrlServiceTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task GenerateAsync_Root_Crl_Contains_Revoked_Intermediate()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var all = await authorities.ListAsync(CancellationToken.None);
        var root = all.Single(authority => authority.Type == "root");
        var intermediate = all.Single(authority => authority.Type == "intermediate");
        var intermediatePath = Path.Combine(storage.RootPath, "authorities", intermediate.Id, "authority.pfx");
        using var intermediateCertificate = X509CertificateLoader.LoadPkcs12FromFile(intermediatePath, null);
        Assert.Contains(intermediateCertificate.Extensions.Cast<X509Extension>(), extension => extension.Oid?.Value == "2.5.29.31");

        await authorities.RevokeAsync(intermediate.Id, CancellationToken.None);
        await crl.GenerateAsync(root.Id, CancellationToken.None);

        var export = await crl.GetAsync(root.Id, CancellationToken.None);
        Assert.NotNull(export);
        var generated = new X509CrlParser().ReadCrl(export.Content);
        Assert.Contains(generated.GetRevokedCertificates().Cast<X509CrlEntry>(), entry =>
            entry.SerialNumber.Equals(new BigInteger(intermediateCertificate.SerialNumber, 16)));
    }

    [Fact]
    public async Task RenewExpiringAsync_Generates_Missing_Root_And_Issuing_Crls()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);

        var renewed = await crl.RenewExpiringAsync(CancellationToken.None);

        Assert.Equal(2, renewed);
        var all = await authorities.ListAsync(CancellationToken.None);
        foreach (var authority in all)
            Assert.NotNull(await crl.GetAsync(authority.Id, CancellationToken.None));
    }

    public void Dispose() => _fixture.Dispose();
}
