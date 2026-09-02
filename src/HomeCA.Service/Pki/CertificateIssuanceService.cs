using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Deployments;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Options;

namespace HomeCA.Service.Pki;

public sealed class CertificateIssuanceService(HomeCaStorage storage, DeploymentPackageService deployments, CertificateAuthorityService authorities, RevocationRegistry revocations, CrlService crl, IOptions<HomeCaStorageOptions> options, ILogger<CertificateIssuanceService> logger)
{
    private readonly string _certificateRoot = Path.Combine(storage.RootPath, "certificates");
    private readonly string _exportRoot = Path.Combine(storage.RootPath, "exports");

    public Task<IReadOnlyList<CertificateInventoryItem>> ListAsync(CancellationToken cancellationToken, string? search = null, int skip = 0, int take = 100)
    {
        if (!Directory.Exists(_certificateRoot)) return Task.FromResult<IReadOnlyList<CertificateInventoryItem>>([]);

        var items = new List<CertificateInventoryItem>();
        foreach (var directory in Directory.EnumerateDirectories(_certificateRoot))
        {
            var id = Path.GetFileName(directory);
            var pfxPath = Path.Combine(directory, "certificate.pfx");
            if (!File.Exists(pfxPath)) continue;

            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
                items.Add(new CertificateInventoryItem(id, certificate.Subject, certificate.NotBefore, certificate.NotAfter, certificate.PublicKey.Oid?.FriendlyName ?? "Unknown", Path.Combine(_exportRoot, id)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load certificate {CertificateId} from {Path}, skipping", id, pfxPath);
            }
        }

        var query = items.OrderBy(item => item.ExpiresAt).AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                item.Subject.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Id.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var result = query.Skip(skip).Take(Math.Clamp(take, 1, 500)).ToList();
        return Task.FromResult<IReadOnlyList<CertificateInventoryItem>>(result);
    }

    /// <summary>Returns detailed certificate metadata including SANs, extensions, fingerprint and issuer chain.</summary>
    public Task<CertificateDetails?> GetDetailsAsync(string id, CancellationToken cancellationToken)
    {
        var pfxPath = Path.Combine(_certificateRoot, id, "certificate.pfx");
        if (!File.Exists(pfxPath)) return Task.FromResult<CertificateDetails?>(null);

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);

        var dnsNames = new List<string>();
        var ipAddresses = new List<string>();
        foreach (var ext in certificate.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue;
            var sanExt = new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical);
            foreach (var name in sanExt.EnumerateDnsNames()) dnsNames.Add(name);
            foreach (var ip in sanExt.EnumerateIPAddresses()) ipAddresses.Add(ip.ToString());
        }

        var keyAlgorithm = certificate.PublicKey.Oid?.Value switch
        {
            "1.2.840.10045.2.1" => "ECC",
            "1.2.840.113549.1.1.1" => "RSA",
            _ => certificate.PublicKey.Oid?.FriendlyName ?? "Unknown"
        };

        var keySize = 0;
        if (keyAlgorithm == "RSA") { using var rsa = certificate.GetRSAPublicKey(); keySize = rsa?.KeySize ?? 0; }
        else if (keyAlgorithm == "ECC") { using var ecc = certificate.GetECDsaPublicKey(); keySize = ecc?.KeySize ?? 0; }

        var usage = "TLS";
        var ekuList = new List<string>();
        foreach (var ext in certificate.Extensions)
        {
            if (ext is X509EnhancedKeyUsageExtension eku)
            {
                foreach (Oid oid in eku.EnhancedKeyUsages)
                {
                    ekuList.Add(oid.FriendlyName ?? oid.Value ?? "Unknown");
                    if (oid.Value == "1.3.6.1.5.5.7.3.2") usage = "mTLS";
                }
                break;
            }
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        var serial = certificate.SerialNumber;

        return Task.FromResult<CertificateDetails?>(new CertificateDetails(
            id, certificate.Subject, certificate.Issuer, serial, sha256,
            certificate.NotBefore, certificate.NotAfter, keyAlgorithm, keySize,
            usage, dnsNames, ipAddresses, ekuList, Path.Combine(_exportRoot, id)));
    }

    /// <summary>Revokes a certificate (adds it to the CRL) and deletes its files from disk.</summary>
    public async Task<bool> RevokeAndDeleteAsync(string id, string reason, CancellationToken cancellationToken)
    {
        var pfxPath = Path.Combine(_certificateRoot, id, "certificate.pfx");
        if (!File.Exists(pfxPath)) return false;

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
        var serialNumber = certificate.SerialNumber;

        logger.LogInformation("Revoking certificate {CertificateId} (serial {SerialNumber}), reason: {Reason}", id, serialNumber, reason);

        var authorityId = await authorities.FindIssuingIdBySubjectAsync(certificate.Issuer, cancellationToken);
        await revocations.RevokeAsync(serialNumber, reason, cancellationToken, authorityId);

        try
        {
            if (authorityId is not null) await crl.GenerateAsync(authorityId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CRL regeneration failed after revoking certificate {CertificateId}; the revocation record is persisted and will be included in the next CRL generation", id);
        }

        var certificateDirectory = Path.Combine(_certificateRoot, id);
        if (Directory.Exists(certificateDirectory)) Directory.Delete(certificateDirectory, true);

        var exportDirectory = Path.Combine(_exportRoot, id);
        if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory, true);

        logger.LogInformation("Deleted certificate {CertificateId} files after revocation", id);
        return true;
    }

    public async Task<IssueResult> IssueAsync(IssueCertificateRequest request, CancellationToken cancellationToken)
    {
        if (request.DnsNames.Count == 0 && request.IpAddresses.Count == 0) throw new ArgumentException("At least one DNS or IP SAN is required.");
        if (request.ValidityDays is < 1 or > 730) throw new ArgumentOutOfRangeException(nameof(request.ValidityDays), "Validity must be between 1 and 730 days.");

        var subject = request.DnsNames.FirstOrDefault() ?? request.IpAddresses.First();
        logger.LogInformation("Issuing {Usage} certificate for {Subject} ({KeyAlgorithm}, {ValidityDays} days)",
            request.Usage, subject, request.KeyAlgorithm, request.ValidityDays);

        var authorityPaths = await authorities.GetDefaultIssuingAsync(cancellationToken);

        using var issuer = X509CertificateLoader.LoadPkcs12FromFile(authorityPaths.IssuingPath, null);
        var requestedNotAfter = DateTimeOffset.UtcNow.AddDays(request.ValidityDays);
        if (issuer.NotAfter <= requestedNotAfter)
            throw new InvalidOperationException($"The issuing CA expires on {issuer.NotAfter:yyyy-MM-dd}; rotate it or choose a shorter certificate validity.");
        using var ecc = request.KeyAlgorithm == "RSA" ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsa = request.KeyAlgorithm == "RSA" ? RSA.Create(request.RsaKeySize is 2048 or 3072 ? request.RsaKeySize : 2048) : null;
        CertificateRequest certificateRequest = ecc is not null
            ? new CertificateRequest($"CN={subject}", ecc, HashAlgorithmName.SHA256)
            : new CertificateRequest(new X500DistinguishedName($"CN={subject}"), rsa!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | (rsa is not null ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.None), true));
        var eku = request.Usage == "mTLS" ? new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") } : new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in request.DnsNames.Distinct(StringComparer.OrdinalIgnoreCase)) san.AddDnsName(name);
        foreach (var ip in request.IpAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!System.Net.IPAddress.TryParse(ip, out var parsed)) throw new ArgumentException($"Invalid IP SAN: {ip}");
            san.AddIpAddress(parsed);
        }
        certificateRequest.CertificateExtensions.Add(san.Build());
        certificateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(certificateRequest.PublicKey, false));
        var publicUrl = options.Value.PublicUrl?.TrimEnd('/');
        if (!string.IsNullOrEmpty(publicUrl))
        {
            certificateRequest.CertificateExtensions.Add(BuildCdpExtension($"{publicUrl}/api/v1/crl/{authorityPaths.Id}"));
        }
        var serial = RandomNumberGenerator.GetBytes(16);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = requestedNotAfter;
        // Use X509SignatureGenerator to handle cross-algorithm signing (e.g. RSA cert signed by ECC CA)
        using var issuerEcc = issuer.GetECDsaPrivateKey();
        using var issuerRsa = issuerEcc is null ? issuer.GetRSAPrivateKey() : null;
        var generator = issuerEcc is not null
            ? X509SignatureGenerator.CreateForECDsa(issuerEcc)
            : X509SignatureGenerator.CreateForRSA(issuerRsa!, RSASignaturePadding.Pkcs1);
        using var unsigned = certificateRequest.Create(issuer.SubjectName, generator, notBefore, notAfter, serial);
        using var certificate = ecc is not null ? unsigned.CopyWithPrivateKey(ecc) : unsigned.CopyWithPrivateKey(rsa!);
        var id = Convert.ToHexString(serial).ToLowerInvariant();
        var certificatePath = Path.Combine(_certificateRoot, id);
        var exportPath = Path.Combine(_exportRoot, id);

        try
        {
            Directory.CreateDirectory(certificatePath);
            Directory.CreateDirectory(exportPath);
            File.WriteAllBytes(Path.Combine(certificatePath, "certificate.pfx"), certificate.Export(X509ContentType.Pkcs12));
            var certPem = certificate.ExportCertificatePem();
            var keyPem = ecc is not null ? ecc.ExportPkcs8PrivateKeyPem() : rsa!.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(Path.Combine(exportPath, "certificate.pem"), certPem);
            File.WriteAllText(Path.Combine(exportPath, "key.pem"), keyPem);
            using var root = X509CertificateLoader.LoadPkcs12FromFile(authorityPaths.RootPath, null);
            var chainPem = issuer.ExportCertificatePem() + "\n" + root.ExportCertificatePem() + "\n";
            File.WriteAllText(Path.Combine(exportPath, "chain.pem"), chainPem);
            File.WriteAllText(Path.Combine(exportPath, "fullchain.pem"), certPem + "\n" + chainPem);
            File.WriteAllText(Path.Combine(exportPath, "bundle.pem"), keyPem + "\n" + certPem + "\n" + chainPem);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write certificate files for {CertificateId}", id);
            throw;
        }

        await deployments.CreateAsync(exportPath, id, request.TargetProfileId, cancellationToken);
        logger.LogInformation("Issued certificate {CertificateId} for {Subject}, valid until {ExpiresAt:yyyy-MM-dd}", id, subject, notAfter);
        return new IssueResult(id, certificate.Subject, certificate.NotAfter, request.Usage, request.KeyAlgorithm, exportPath);
    }

    /// <summary>Builds an X.509 CRL Distribution Points extension (OID 2.5.29.31) containing a single HTTP URI.</summary>
    private static X509Extension BuildCdpExtension(string url)
    {
        // ASN.1 structure: SEQUENCE { SEQUENCE { DistributionPoint { distributionPoint [0] { fullName [0] { GeneralName uniformResourceIdentifier [6] url } } } } }
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // CRLDistributionPoints ::= SEQUENCE OF DistributionPoint
        {
            using (writer.PushSequence()) // DistributionPoint ::= SEQUENCE
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true))) // distributionPoint [0]
                {
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true))) // fullName [0]
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, url, new Asn1Tag(TagClass.ContextSpecific, 6)); // uniformResourceIdentifier [6]
                    }
                }
            }
        }
        return new X509Extension("2.5.29.31", writer.Encode(), critical: false);
    }
}

public sealed record IssueCertificateRequest(string Usage, IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses, int ValidityDays = 365, string KeyAlgorithm = "ECC", int RsaKeySize = 2048, string? TargetProfileId = null);
public sealed record IssueResult(string Id, string Subject, DateTime ExpiresAt, string Usage, string KeyAlgorithm, string ExportPath);
public sealed record CertificateInventoryItem(string Id, string Subject, DateTime ValidFrom, DateTime ExpiresAt, string KeyAlgorithm, string ExportPath);
public sealed record CertificateDetails(string Id, string Subject, string Issuer, string SerialNumber, string Sha256Fingerprint, DateTime ValidFrom, DateTime ExpiresAt, string KeyAlgorithm, int KeySize, string Usage, IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses, IReadOnlyList<string> EnhancedKeyUsages, string ExportPath);
public sealed record PfxExportRequest(string Password);
