using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using HomeCA.Service.Revocation;

namespace HomeCA.Service.Pki;

public sealed class CrlService(HomeCaStorage storage, RevocationRegistry revocations, CertificateAuthorityService authorities, ILogger<CrlService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> GenerateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var authority = await authorities.GetDefaultIssuingAsync(cancellationToken);
            using var issuer = CertificatePfxExporter.LoadCertificateWithExportablePrivateKey(authority.IssuingPath);
            using var ecdsa = issuer.GetECDsaPrivateKey();
            using var rsa = ecdsa is null ? issuer.GetRSAPrivateKey() : null;
            if (ecdsa is null && rsa is null)
                throw new InvalidOperationException("TLS issuing CA private key is unavailable.");
            var parser = new X509CertificateParser();
            var generator = new X509V2CrlGenerator();
            generator.SetIssuerDN(parser.ReadCertificate(issuer.RawData).SubjectDN);
            generator.SetThisUpdate(DateTime.UtcNow);
            generator.SetNextUpdate(DateTime.UtcNow.AddDays(authority.CrlValidityDays));
            var revocationRecords = await revocations.ListAsync(cancellationToken);
            foreach (var record in revocationRecords)
                generator.AddCrlEntry(new BigInteger(record.SerialNumber, 16), record.RevokedAt.UtcDateTime, 0);
            // DotNetUtilities cannot convert ECDsa keys. Import the PKCS#8 key
            // material through BouncyCastle instead; this also supports RSA CAs.
            var privateKey = ecdsa is not null
                ? PrivateKeyFactory.CreateKey(ecdsa.ExportPkcs8PrivateKey())
                : PrivateKeyFactory.CreateKey(rsa!.ExportPkcs8PrivateKey());
            var signatureAlgorithm = ecdsa is not null ? "SHA384WITHECDSA" : "SHA384WITHRSA";
            var signer = new Asn1SignatureFactory(signatureAlgorithm, privateKey);
            var crl = generator.Generate(signer);
            var path = Path.Combine(storage.RootPath, "crl", $"{authority.Id}.crl");
            await File.WriteAllBytesAsync(path, crl.GetEncoded(), cancellationToken);
            logger.LogInformation("Generated CRL with {Count} entries at {Path}", revocationRecords.Count, path);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            logger.LogError(ex, "Failed to generate CRL");
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns the most recently generated CRL for the default issuing CA, or null if none exists.</summary>
    public async Task<CrlExport?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var authority = await authorities.GetDefaultIssuingAsync(cancellationToken);
        var path = Path.Combine(storage.RootPath, "crl", $"{authority.Id}.crl");
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new CrlExport($"{authority.Id}.crl", bytes);
    }
}

public sealed record CrlExport(string FileName, byte[] Content);
