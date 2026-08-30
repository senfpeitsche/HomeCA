using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Deployments;

namespace HomeCA.Service.Pki;

public sealed class CertificateIssuanceService(HomeCaStorage storage, DeploymentPackageService deployments, CertificateAuthorityService authorities)
{
    private readonly string _certificateRoot = Path.Combine(storage.RootPath, "certificates");
    private readonly string _exportRoot = Path.Combine(storage.RootPath, "exports");

    public Task<IReadOnlyList<CertificateInventoryItem>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_certificateRoot)) return Task.FromResult<IReadOnlyList<CertificateInventoryItem>>([]);
        var items = Directory.EnumerateDirectories(_certificateRoot)
            .Select(directory => new { Id = Path.GetFileName(directory), Path = Path.Combine(directory, "certificate.pfx") })
            .Where(item => File.Exists(item.Path))
            .Select(item =>
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(item.Path, null);
                return new CertificateInventoryItem(item.Id, certificate.Subject, certificate.NotBefore, certificate.NotAfter, certificate.PublicKey.Oid?.FriendlyName ?? "Unknown", Path.Combine(_exportRoot, item.Id));
            })
            .OrderBy(item => item.ExpiresAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<CertificateInventoryItem>>(items);
    }

    public async Task<IssueResult> IssueAsync(IssueCertificateRequest request, CancellationToken cancellationToken)
    {
        if (request.DnsNames.Count == 0 && request.IpAddresses.Count == 0) throw new ArgumentException("At least one DNS or IP SAN is required.");
        if (request.ValidityDays is < 1 or > 730) throw new ArgumentOutOfRangeException(nameof(request.ValidityDays), "Validity must be between 1 and 730 days.");
        var authorityPaths = await authorities.GetDefaultIssuingAsync(cancellationToken);

        using var issuer = X509CertificateLoader.LoadPkcs12FromFile(authorityPaths.IssuingPath, null);
        using var ecc = request.KeyAlgorithm == "RSA" ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsa = request.KeyAlgorithm == "RSA" ? RSA.Create(request.RsaKeySize is 2048 or 3072 ? request.RsaKeySize : 2048) : null;
        var subject = request.DnsNames.FirstOrDefault() ?? request.IpAddresses.First();
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
        var serial = RandomNumberGenerator.GetBytes(16);
        using var unsigned = certificateRequest.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(request.ValidityDays), serial);
        using var certificate = ecc is not null ? unsigned.CopyWithPrivateKey(ecc) : unsigned.CopyWithPrivateKey(rsa!);
        var id = Convert.ToHexString(serial).ToLowerInvariant();
        var certificatePath = Path.Combine(_certificateRoot, id);
        var exportPath = Path.Combine(_exportRoot, id);
        Directory.CreateDirectory(certificatePath);
        Directory.CreateDirectory(exportPath);
        File.WriteAllBytes(Path.Combine(certificatePath, "certificate.pfx"), certificate.Export(X509ContentType.Pkcs12));
        File.WriteAllText(Path.Combine(exportPath, "certificate.pem"), certificate.ExportCertificatePem());
        using var root = X509CertificateLoader.LoadPkcs12FromFile(authorityPaths.RootPath, null);
        File.WriteAllText(Path.Combine(exportPath, "chain.pem"), issuer.ExportCertificatePem() + root.ExportCertificatePem());
        await deployments.CreateAsync(exportPath, id, request.TargetProfileId, cancellationToken);
        return new IssueResult(id, certificate.Subject, certificate.NotAfter, request.Usage, request.KeyAlgorithm, exportPath);
    }
}

public sealed record IssueCertificateRequest(string Usage, IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses, int ValidityDays = 365, string KeyAlgorithm = "ECC", int RsaKeySize = 2048, string? TargetProfileId = null);
public sealed record IssueResult(string Id, string Subject, DateTime ExpiresAt, string Usage, string KeyAlgorithm, string ExportPath);
public sealed record CertificateInventoryItem(string Id, string Subject, DateTime ValidFrom, DateTime ExpiresAt, string KeyAlgorithm, string ExportPath);
