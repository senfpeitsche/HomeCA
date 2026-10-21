using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Deployments;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Options;

namespace HomeCA.Service.Pki;

public sealed class CertificateIssuanceService(HomeCaStorage storage, DeploymentPackageService deployments, CertificateAuthorityService authorities, RevocationRegistry revocations, CrlService crl, IOptions<HomeCaStorageOptions> options, ILogger<CertificateIssuanceService> logger)
{
    private readonly string _certificateRoot = Path.Combine(storage.RootPath, "certificates");
    private readonly string _exportRoot = Path.Combine(storage.RootPath, "exports");
    private readonly string _inventoryPath = Path.Combine(storage.RootPath, "certificates", "inventory.json");
    private readonly SemaphoreSlim _inventoryGate = new(1, 1);

    public async Task<IReadOnlyList<CertificateInventoryItem>> ListAsync(CancellationToken cancellationToken, string? search = null, int skip = 0, int take = 100)
    {
        var items = await LoadInventoryAsync(cancellationToken);

        var query = items.OrderBy(item => item.ExpiresAt).AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                item.Subject.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Id.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var result = query.Skip(skip).Take(Math.Clamp(take, 1, 500)).ToList();
        return result;
    }

    /// <summary>Adds a certificate already retained in HomeCA storage to the shared inventory.
    /// This is used for ACME certificates, whose private key remains with the ACME client.</summary>
    public async Task RegisterExistingCertificateAsync(string id, CancellationToken cancellationToken)
    {
        var pfxPath = Path.Combine(_certificateRoot, id, "certificate.pfx");
        if (!File.Exists(pfxPath)) throw new FileNotFoundException("Certificate file was not found.", pfxPath);

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
        await AddToInventoryAsync(new CertificateInventoryItem(
            id,
            certificate.Subject,
            certificate.NotBefore,
            certificate.NotAfter,
            certificate.PublicKey.Oid?.FriendlyName ?? "Unknown",
            Path.Combine(_exportRoot, id)), cancellationToken);
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

    /// <summary>Revokes a certificate and retains its certificate and export records for audit and forensic use.</summary>
    public async Task<bool> RevokeAsync(string id, string reason, CancellationToken cancellationToken)
    {
        var pfxPath = Path.Combine(_certificateRoot, id, "certificate.pfx");
        if (!File.Exists(pfxPath)) return false;

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
        var serialNumber = certificate.SerialNumber;

        logger.LogInformation("Revoking certificate {CertificateId} (serial {SerialNumber}), reason: {Reason}", id, serialNumber, reason);

        var authorityId = await authorities.FindIssuingIdAsync(certificate, cancellationToken);
        await revocations.RevokeAsync(serialNumber, reason, cancellationToken, authorityId);

        try
        {
            if (authorityId is not null) await crl.GenerateAsync(authorityId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CRL regeneration failed after revoking certificate {CertificateId}; the revocation record is persisted and will be included in the next CRL generation", id);
        }

        logger.LogInformation("Revoked certificate {CertificateId}; certificate and export records were retained for audit", id);
        return true;
    }

    public async Task<IssueResult> IssueAsync(IssueCertificateRequest request, CancellationToken cancellationToken)
    {
        ValidateNames(request.DnsNames, request.IpAddresses, request.ValidityDays);
        var subject = request.DnsNames.FirstOrDefault() ?? request.IpAddresses.First();
        logger.LogInformation("Issuing {Usage} certificate for {Subject} ({KeyAlgorithm}, {ValidityDays} days)",
            request.Usage, subject, request.KeyAlgorithm, request.ValidityDays);

        using var ecc = request.KeyAlgorithm == "RSA" ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsa = request.KeyAlgorithm == "RSA" ? RSA.Create(request.RsaKeySize is 2048 or 3072 ? request.RsaKeySize : 2048) : null;
        var publicKey = ecc is not null ? new PublicKey(ecc) : new PublicKey(rsa!);
        var signed = await SignAsync(publicKey, request.Usage, request.DnsNames, request.IpAddresses, request.ValidityDays, cancellationToken);
        using var unsigned = signed.Certificate;
        using var certificate = ecc is not null ? unsigned.CopyWithPrivateKey(ecc) : unsigned.CopyWithPrivateKey(rsa!);
        var keyPem = ecc is not null ? ecc.ExportPkcs8PrivateKeyPem() : rsa!.ExportPkcs8PrivateKeyPem();

        var exportPath = WriteFiles(certificate, signed.Id, keyPem, signed.ChainPem, csrPem: null);
        await FinishAsync(certificate, signed.Id, exportPath, request.TargetProfileId, cancellationToken);
        return new IssueResult(signed.Id, certificate.Subject, certificate.NotAfter, request.Usage, request.KeyAlgorithm, exportPath);
    }

    /// <summary>
    /// Signs a certificate signing request from a device that keeps its own private key. Only the public key is
    /// taken from the CSR; subject, SANs and extensions are set by HomeCA, so a CSR cannot request CA rights.
    /// </summary>
    public async Task<IssueResult> SignCsrAsync(SignCsrRequest request, CancellationToken cancellationToken)
    {
        CertificateRequest csr;
        try
        {
            // The extensions are only read to take over requested SANs; the signature is always verified.
            csr = CertificateRequest.LoadSigningRequestPem(request.CsrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException($"The CSR is invalid or its signature does not verify: {exception.Message}");
        }

        var (dnsNames, ipAddresses) = request.DnsNames is { Count: > 0 } || request.IpAddresses is { Count: > 0 }
            ? (request.DnsNames ?? [], request.IpAddresses ?? [])
            : RequestedNames(csr);
        ValidateNames(dnsNames, ipAddresses, request.ValidityDays);
        var keyAlgorithm = ValidateCsrKey(csr.PublicKey);
        logger.LogInformation("Signing CSR for {Subject} ({KeyAlgorithm}, {ValidityDays} days)", dnsNames.FirstOrDefault() ?? ipAddresses.First(), keyAlgorithm, request.ValidityDays);

        var signed = await SignAsync(csr.PublicKey, request.Usage, dnsNames, ipAddresses, request.ValidityDays, cancellationToken);
        using var certificate = signed.Certificate;
        var exportPath = WriteFiles(certificate, signed.Id, keyPem: null, signed.ChainPem, request.CsrPem.Trim() + "\n");
        await FinishAsync(certificate, signed.Id, exportPath, request.TargetProfileId, cancellationToken);
        return new IssueResult(signed.Id, certificate.Subject, certificate.NotAfter, request.Usage, keyAlgorithm, exportPath);
    }

    private static void ValidateNames(IReadOnlyList<string> dnsNames, IReadOnlyList<string> ipAddresses, int validityDays)
    {
        if (dnsNames.Count == 0 && ipAddresses.Count == 0) throw new ArgumentException("At least one DNS or IP SAN is required.");
        if (validityDays is < 1 or > 730) throw new ArgumentOutOfRangeException(nameof(validityDays), "Validity must be between 1 and 730 days.");
    }

    private static (IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses) RequestedNames(CertificateRequest csr)
    {
        var dnsNames = new List<string>();
        var ipAddresses = new List<string>();
        foreach (var extension in csr.CertificateExtensions.Where(extension => extension.Oid?.Value == "2.5.29.17"))
        {
            var san = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
            dnsNames.AddRange(san.EnumerateDnsNames());
            ipAddresses.AddRange(san.EnumerateIPAddresses().Select(address => address.ToString()));
        }
        if (dnsNames.Count == 0 && ipAddresses.Count == 0)
        {
            // Older devices only put the host name into the CN.
            var commonName = csr.SubjectName.EnumerateRelativeDistinguishedNames()
                .FirstOrDefault(name => name.GetSingleElementType().Value == "2.5.4.3")?.GetSingleElementValue();
            if (System.Net.IPAddress.TryParse(commonName, out var address)) ipAddresses.Add(address.ToString());
            else if (!string.IsNullOrWhiteSpace(commonName) && Uri.CheckHostName(commonName) == UriHostNameType.Dns) dnsNames.Add(commonName);
        }
        return (dnsNames, ipAddresses);
    }

    private static string ValidateCsrKey(PublicKey publicKey)
    {
        if (publicKey.GetRSAPublicKey() is { } rsa)
        {
            using (rsa) if (rsa.KeySize < 2048) throw new ArgumentException("RSA keys in a CSR must have at least 2048 bits.");
            return "RSA";
        }
        if (publicKey.GetECDsaPublicKey() is { } ecdsa)
        {
            using (ecdsa) if (ecdsa.KeySize < 256) throw new ArgumentException("EC keys in a CSR must use at least P-256.");
            return "ECC";
        }
        throw new ArgumentException("Only RSA and EC keys are supported in a CSR.");
    }

    private async Task<SignedCertificate> SignAsync(PublicKey publicKey, string usage, IReadOnlyList<string> dnsNames, IReadOnlyList<string> ipAddresses, int validityDays, CancellationToken cancellationToken)
    {
        var authorityPaths = await authorities.GetDefaultIssuingAsync(cancellationToken);
        using var issuer = authorities.LoadAuthorityCertificate(authorityPaths.IssuingPath);
        var notAfter = DateTimeOffset.UtcNow.AddDays(validityDays);
        if (issuer.NotAfter <= notAfter)
            throw new InvalidOperationException($"The issuing CA expires on {issuer.NotAfter:yyyy-MM-dd}; rotate it or choose a shorter certificate validity.");
        NameConstraints.EnsurePermitted(issuer, dnsNames, ipAddresses);

        var subject = dnsNames.FirstOrDefault() ?? ipAddresses.First();
        var certificateRequest = new CertificateRequest(new X500DistinguishedName($"CN={subject}"), publicKey, HashAlgorithmName.SHA256);
        var isRsa = publicKey.Oid.Value == "1.2.840.113549.1.1.1";
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | (isRsa ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.None), true));
        var eku = usage == "mTLS" ? new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") } : new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames.Distinct(StringComparer.OrdinalIgnoreCase)) san.AddDnsName(name);
        foreach (var ip in ipAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!System.Net.IPAddress.TryParse(ip, out var parsed)) throw new ArgumentException($"Invalid IP SAN: {ip}");
            san.AddIpAddress(parsed);
        }
        certificateRequest.CertificateExtensions.Add(san.Build());
        certificateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(certificateRequest.PublicKey, false));
        certificateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        var publicUrl = options.Value.CrlDistributionBaseUrl;
        if (!string.IsNullOrEmpty(publicUrl))
        {
            certificateRequest.CertificateExtensions.Add(BuildCdpExtension($"{publicUrl}/api/v1/crl/{authorityPaths.Id}"));
        }
        var serial = RandomNumberGenerator.GetBytes(16);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        // Use X509SignatureGenerator to handle cross-algorithm signing (e.g. RSA cert signed by ECC CA)
        using var issuerEcc = issuer.GetECDsaPrivateKey();
        using var issuerRsa = issuerEcc is null ? issuer.GetRSAPrivateKey() : null;
        var generator = issuerEcc is not null
            ? X509SignatureGenerator.CreateForECDsa(issuerEcc)
            : X509SignatureGenerator.CreateForRSA(issuerRsa!, RSASignaturePadding.Pkcs1);
        var certificate = certificateRequest.Create(issuer.SubjectName, generator, notBefore, notAfter, serial);
        using var root = authorities.LoadAuthorityCertificate(authorityPaths.RootPath);
        return new SignedCertificate(certificate, issuer.ExportCertificatePem() + "\n" + root.ExportCertificatePem() + "\n", Convert.ToHexString(serial).ToLowerInvariant());
    }

    /// <summary>Writes the stored PFX and the exports. Without a key (CSR), key.pem and bundle.pem are omitted.</summary>
    private string WriteFiles(X509Certificate2 certificate, string id, string? keyPem, string chainPem, string? csrPem)
    {
        var certificatePath = Path.Combine(_certificateRoot, id);
        var exportPath = Path.Combine(_exportRoot, id);
        try
        {
            Directory.CreateDirectory(certificatePath);
            Directory.CreateDirectory(exportPath);
            File.WriteAllBytes(Path.Combine(certificatePath, "certificate.pfx"), certificate.Export(X509ContentType.Pkcs12));
            var certPem = certificate.ExportCertificatePem();
            File.WriteAllText(Path.Combine(exportPath, "certificate.pem"), certPem);
            File.WriteAllText(Path.Combine(exportPath, "chain.pem"), chainPem);
            File.WriteAllText(Path.Combine(exportPath, "fullchain.pem"), certPem + "\n" + chainPem);
            if (keyPem is not null)
            {
                File.WriteAllText(Path.Combine(exportPath, "key.pem"), keyPem);
                File.WriteAllText(Path.Combine(exportPath, "bundle.pem"), keyPem + "\n" + certPem + "\n" + chainPem);
            }
            if (csrPem is not null) File.WriteAllText(Path.Combine(exportPath, "request.csr"), csrPem);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write certificate files for {CertificateId}", id);
            throw;
        }
        return exportPath;
    }

    private async Task FinishAsync(X509Certificate2 certificate, string id, string exportPath, string? targetProfileId, CancellationToken cancellationToken)
    {
        await deployments.CreateAsync(exportPath, id, targetProfileId, cancellationToken);
        await AddToInventoryAsync(new CertificateInventoryItem(id, certificate.Subject, certificate.NotBefore, certificate.NotAfter, certificate.PublicKey.Oid?.FriendlyName ?? "Unknown", exportPath), cancellationToken);
        logger.LogInformation("Issued certificate {CertificateId} for {Subject}, valid until {ExpiresAt:yyyy-MM-dd}", id, certificate.Subject, certificate.NotAfter);
    }

    /// <summary>The ID is the hex serial number as generated; it names the storage directories.</summary>
    private sealed record SignedCertificate(X509Certificate2 Certificate, string ChainPem, string Id);

    private async Task<List<CertificateInventoryItem>> LoadInventoryAsync(CancellationToken ct)
    {
        await _inventoryGate.WaitAsync(ct);
        try { return await LoadInventoryUnsafeAsync(ct); }
        finally { _inventoryGate.Release(); }
    }
    /// <summary>Reads and reconciles the inventory; the caller must hold <c>_inventoryGate</c>.</summary>
    private async Task<List<CertificateInventoryItem>> LoadInventoryUnsafeAsync(CancellationToken ct)
    {
        var items = new List<CertificateInventoryItem>();
        if (File.Exists(_inventoryPath))
        {
            await using var stream = File.OpenRead(_inventoryPath);
            items = await JsonSerializer.DeserializeAsync<List<CertificateInventoryItem>>(stream, cancellationToken: ct) ?? [];
        }
        if (!Directory.Exists(_certificateRoot)) return items;

        // ACME certificates are retained without a private key. Older versions
        // wrote those files but did not add an inventory entry; reconcile any
        // retained certificate missing from the index on every inventory load.
        var knownIds = items.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var directory in Directory.EnumerateDirectories(_certificateRoot))
        {
            var pfxPath = Path.Combine(directory, "certificate.pfx"); if (!File.Exists(pfxPath)) continue;
            var id = Path.GetFileName(directory);
            if (knownIds.Contains(id)) continue;
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
            items.Add(new(id, certificate.Subject, certificate.NotBefore, certificate.NotAfter, certificate.PublicKey.Oid?.FriendlyName ?? "Unknown", Path.Combine(_exportRoot, id)));
            changed = true;
        }
        if (changed || !File.Exists(_inventoryPath)) await WriteInventoryAsync(items, ct);
        return items;
    }
    private async Task AddToInventoryAsync(CertificateInventoryItem item, CancellationToken ct)
    {
        // Read-modify-write under one lock; releasing it in between let concurrent issuances overwrite each other.
        await _inventoryGate.WaitAsync(ct);
        try
        {
            var items = await LoadInventoryUnsafeAsync(ct);
            items.RemoveAll(x => x.Id == item.Id); items.Add(item); await WriteInventoryAsync(items, ct);
        }
        finally { _inventoryGate.Release(); }
    }
    private async Task WriteInventoryAsync(List<CertificateInventoryItem> items, CancellationToken ct)
    { var tmp = _inventoryPath + ".tmp"; await using (var stream = File.Create(tmp)) await JsonSerializer.SerializeAsync(stream, items, cancellationToken: ct); File.Move(tmp, _inventoryPath, true); }

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
/// <summary>Issuance request of the management API. Unlike <see cref="IssueCertificateRequest"/> it also creates a renewal plan unless <paramref name="AutoRenew"/> is false.</summary>
public sealed record IssueCertificateApiRequest(string Usage, IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses, int ValidityDays = 365, string KeyAlgorithm = "ECC", int RsaKeySize = 2048, string? TargetProfileId = null, bool AutoRenew = true, int? RenewBeforeDays = null)
{
    public IssueCertificateRequest ToIssueRequest() => new(Usage, DnsNames, IpAddresses, ValidityDays, KeyAlgorithm, RsaKeySize, TargetProfileId);
}
public sealed record IssueResult(string Id, string Subject, DateTime ExpiresAt, string Usage, string KeyAlgorithm, string ExportPath, string? RenewalPlanId = null);
public sealed record CertificateInventoryItem(string Id, string Subject, DateTime ValidFrom, DateTime ExpiresAt, string KeyAlgorithm, string ExportPath);
public sealed record CertificateDetails(string Id, string Subject, string Issuer, string SerialNumber, string Sha256Fingerprint, DateTime ValidFrom, DateTime ExpiresAt, string KeyAlgorithm, int KeySize, string Usage, IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses, IReadOnlyList<string> EnhancedKeyUsages, string ExportPath);
public sealed record PfxExportRequest(string Password);
/// <summary>Signs a CSR. Without DNS names or IP addresses, the SANs requested in the CSR (or its CN) are used.</summary>
public sealed record SignCsrRequest(string CsrPem, string Usage = "TLS", int ValidityDays = 365, IReadOnlyList<string>? DnsNames = null, IReadOnlyList<string>? IpAddresses = null, string? TargetProfileId = null);
