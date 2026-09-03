using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;
using HomeCA.Service.Revocation;

namespace HomeCA.Service.Pki;

public sealed class CrlService(HomeCaStorage storage, RevocationRegistry revocations, CertificateAuthorityService authorities, ILogger<CrlService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> GenerateAsync(CancellationToken cancellationToken)
    {
        var authority = await authorities.GetDefaultIssuingAsync(cancellationToken);
        return await GenerateAsync(authority.Id, cancellationToken);
    }

    public async Task<string> GenerateAsync(string authorityId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var authority = await authorities.GetCrlAuthorityAsync(authorityId, cancellationToken);
            using var issuer = CertificatePfxExporter.LoadCertificateWithExportablePrivateKey(authority.AuthorityPath, storage.GetCaPfxPassword());
            using var ecdsa = issuer.GetECDsaPrivateKey();
            using var rsa = ecdsa is null ? issuer.GetRSAPrivateKey() : null;
            if (ecdsa is null && rsa is null)
                throw new InvalidOperationException("TLS issuing CA private key is unavailable.");
            var parser = new X509CertificateParser();
            var issuerCertificate = parser.ReadCertificate(issuer.RawData);
            var generator = new X509V2CrlGenerator();
            generator.SetIssuerDN(issuerCertificate.SubjectDN);
            generator.SetThisUpdate(DateTime.UtcNow);
            generator.SetNextUpdate(DateTime.UtcNow.AddDays(authority.CrlValidityDays));
            var path = Path.Combine(storage.RootPath, "crl", $"{authority.Id}.crl");
            generator.AddExtension(X509Extensions.CrlNumber, false, new CrlNumber(GetNextCrlNumber(path)));
            generator.AddExtension(X509Extensions.AuthorityKeyIdentifier, false, new AuthorityKeyIdentifierStructure(issuerCertificate));
            var revocationRecords = authority.Type == "root"
                ? (await authorities.GetRevokedIntermediatesAsync(authority.Id, cancellationToken))
                    .Select(record => new CrlEntry(record.SerialNumber, record.RevokedAt, CrlReason.Unspecified))
                    .ToList()
                : await GetCertificateRevocationsAsync(authority.Id, cancellationToken);
            foreach (var record in revocationRecords)
                generator.AddCrlEntry(new BigInteger(record.SerialNumber, 16), record.RevokedAt.UtcDateTime, record.ReasonCode);
            // DotNetUtilities cannot convert ECDsa keys. Import the PKCS#8 key
            // material through BouncyCastle instead; this also supports RSA CAs.
            var privateKey = ecdsa is not null
                ? PrivateKeyFactory.CreateKey(ecdsa.ExportPkcs8PrivateKey())
                : PrivateKeyFactory.CreateKey(rsa!.ExportPkcs8PrivateKey());
            var signatureAlgorithm = ecdsa is not null ? "SHA384WITHECDSA" : "SHA384WITHRSA";
            var signer = new Asn1SignatureFactory(signatureAlgorithm, privateKey);
            var crl = generator.Generate(signer);
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
        return await GetAsync(authority.Id, cancellationToken);
    }

    public async Task<CrlExport?> GetAsync(string authorityId, CancellationToken cancellationToken)
    {
        var authority = await authorities.GetCrlAuthorityAsync(authorityId, cancellationToken);
        var path = Path.Combine(storage.RootPath, "crl", $"{authority.Id}.crl");
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new CrlExport($"{authority.Id}.crl", bytes);
    }

    /// <summary>Creates missing CRLs and replaces CRLs that are past half of their configured lifetime.</summary>
    public async Task<int> RenewExpiringAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var authoritiesToRenew = (await authorities.ListAsync(cancellationToken))
            .Where(authority => authority.Type is "root" or "intermediate" && !authority.IsRevoked)
            .ToList();
        var renewed = 0;
        foreach (var authority in authoritiesToRenew)
        {
            var path = Path.Combine(storage.RootPath, "crl", $"{authority.Id}.crl");
            var renewAfter = now.AddDays(authority.CrlValidityDays / 2d);
            if (File.Exists(path))
            {
                try
                {
                    var existing = new X509CrlParser().ReadCrl(await File.ReadAllBytesAsync(path, cancellationToken));
                    if (existing.NextUpdate is { } nextUpdate && nextUpdate.Value.ToUniversalTime() > renewAfter) continue;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "CRL at {Path} could not be read and will be regenerated", path);
                }
            }
            await GenerateAsync(authority.Id, cancellationToken);
            renewed++;
        }
        return renewed;
    }

    private async Task<List<CrlEntry>> GetCertificateRevocationsAsync(string authorityId, CancellationToken cancellationToken)
    {
        var defaultAuthority = await authorities.GetDefaultIssuingAsync(cancellationToken);
        return (await revocations.ListAsync(cancellationToken))
            .Where(record => record.AuthorityId == authorityId || record.AuthorityId is null && authorityId == defaultAuthority.Id)
            .Select(record => new CrlEntry(record.SerialNumber, record.RevokedAt, ToCrlReasonCode(record.Reason)))
            .ToList();
    }

    private BigInteger GetNextCrlNumber(string path)
    {
        if (!File.Exists(path)) return BigInteger.One;

        try
        {
            var crl = new X509CrlParser().ReadCrl(File.ReadAllBytes(path));
            var extension = crl.GetExtensionValue(X509Extensions.CrlNumber);
            if (extension is null) return BigInteger.One;
            return CrlNumber.GetInstance(Asn1Object.FromByteArray(extension.GetOctets())).Value.Add(BigInteger.One);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the previous CRL number from {Path}; restarting at 1", path);
            return BigInteger.One;
        }
    }

    private static int ToCrlReasonCode(string reason) => reason.ToLowerInvariant() switch
    {
        "keycompromise" => CrlReason.KeyCompromise,
        "cacompromise" => CrlReason.CACompromise,
        "affiliationchanged" => CrlReason.AffiliationChanged,
        "superseded" => CrlReason.Superseded,
        "cessationofoperation" => CrlReason.CessationOfOperation,
        "certificatehold" => CrlReason.CertificateHold,
        "removefromcrl" => CrlReason.RemoveFromCrl,
        "privilegewithdrawn" => CrlReason.PrivilegeWithdrawn,
        "aacompromise" => CrlReason.AACompromise,
        _ => CrlReason.Unspecified
    };

    private sealed record CrlEntry(string SerialNumber, DateTimeOffset RevokedAt, int ReasonCode);
}

public sealed record CrlExport(string FileName, byte[] Content);
