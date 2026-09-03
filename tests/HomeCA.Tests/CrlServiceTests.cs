using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Pki;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
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
        using var intermediateCertificate = X509CertificateLoader.LoadPkcs12FromFile(intermediatePath, storage.GetCaPfxPassword());
        Assert.Contains(intermediateCertificate.Extensions.Cast<System.Security.Cryptography.X509Certificates.X509Extension>(), extension => extension.Oid?.Value == "2.5.29.31");

        await authorities.RevokeAsync(intermediate.Id, CancellationToken.None);
        await crl.GenerateAsync(root.Id, CancellationToken.None);

        var export = await crl.GetAsync(root.Id, CancellationToken.None);
        Assert.NotNull(export);
        var generated = new X509CrlParser().ReadCrl(export.Content);
        Assert.NotNull(generated.GetExtensionValue(X509Extensions.CrlNumber));
        Assert.NotNull(generated.GetExtensionValue(X509Extensions.AuthorityKeyIdentifier));
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

    [Fact]
    public async Task GenerateAsync_Increments_CrlNumber_And_Preserves_RevocationReason()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var issuing = (await authorities.ListAsync(CancellationToken.None)).Single(authority => authority.Type == "intermediate");
        await revocations.RevokeAsync("01", "keyCompromise", CancellationToken.None, issuing.Id);

        await crl.GenerateAsync(issuing.Id, CancellationToken.None);
        var first = new X509CrlParser().ReadCrl((await crl.GetAsync(issuing.Id, CancellationToken.None))!.Content);
        await crl.GenerateAsync(issuing.Id, CancellationToken.None);
        var second = new X509CrlParser().ReadCrl((await crl.GetAsync(issuing.Id, CancellationToken.None))!.Content);

        Assert.Equal(GetCrlNumber(first).Add(BigInteger.One), GetCrlNumber(second));
        var entry = Assert.Single(second.GetRevokedCertificates().Cast<X509CrlEntry>());
        var reason = entry.GetExtensionValue(X509Extensions.ReasonCode);
        Assert.NotNull(reason);
        Assert.Equal(CrlReason.KeyCompromise, CrlReason.GetInstance(Asn1Object.FromByteArray(reason.GetOctets())).Value.IntValue);
    }

    private static BigInteger GetCrlNumber(X509Crl crl)
    {
        var extension = crl.GetExtensionValue(X509Extensions.CrlNumber);
        Assert.NotNull(extension);
        return CrlNumber.GetInstance(Asn1Object.FromByteArray(extension.GetOctets())).Value;
    }

    public void Dispose() => _fixture.Dispose();
}
