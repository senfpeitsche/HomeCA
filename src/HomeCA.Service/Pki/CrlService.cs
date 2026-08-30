using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using HomeCA.Service.Revocation;

namespace HomeCA.Service.Pki;

public sealed class CrlService(HomeCaStorage storage, RevocationRegistry revocations, CertificateAuthorityService authorities)
{
    public async Task<string> GenerateAsync(CancellationToken cancellationToken)
    {
        var authority = await authorities.GetDefaultIssuingAsync(cancellationToken);
        using var issuer = X509CertificateLoader.LoadPkcs12FromFile(authority.IssuingPath, null);
        using var key = issuer.GetECDsaPrivateKey() ?? throw new InvalidOperationException("TLS issuing CA private key is unavailable.");
        var parser = new X509CertificateParser();
        var generator = new X509V2CrlGenerator();
        generator.SetIssuerDN(parser.ReadCertificate(issuer.RawData).SubjectDN);
        generator.SetThisUpdate(DateTime.UtcNow);
        generator.SetNextUpdate(DateTime.UtcNow.AddDays(authority.CrlValidityDays));
        foreach (var record in await revocations.ListAsync(cancellationToken)) generator.AddCrlEntry(new BigInteger(record.SerialNumber, 16), record.RevokedAt.UtcDateTime, 0);
        var signer = new Asn1SignatureFactory("SHA384WITHECDSA", DotNetUtilities.GetKeyPair(key).Private);
        var crl = generator.Generate(signer);
        var path = Path.Combine(storage.RootPath, "crl", $"{authority.Id}.crl");
        await File.WriteAllBytesAsync(path, crl.GetEncoded(), cancellationToken);
        return path;
    }
}
