using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using System.Formats.Asn1;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Pki;

public sealed class CertificateAuthorityService(HomeCaStorage storage, ILogger<CertificateAuthorityService> logger)
{
    private readonly string _root = Path.Combine(storage.RootPath, "authorities");
    private readonly string _statePath = Path.Combine(storage.RootPath, "state", "authorities.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<CertificateAuthorityInventoryItem>> ListAsync(CancellationToken ct) =>
        (await ReadAsync(ct)).Select(ToItem).ToList();

    public async Task<AuthorityCertificateExport?> ExportCertificateAsync(string id, string format, CancellationToken ct)
    {
        var authority = (await ReadAsync(ct)).SingleOrDefault(item => item.Id == id);
        if (authority is null) return null;
        using var certificate = Load(authority);
        return format.ToLowerInvariant() switch
        {
            "pem" => new AuthorityCertificateExport($"{authority.Name}.pem", "application/x-pem-file", Encoding.UTF8.GetBytes(certificate.ExportCertificatePem())),
            "der" => new AuthorityCertificateExport($"{authority.Name}.cer", "application/pkix-cert", certificate.Export(X509ContentType.Cert)),
            _ => throw new ArgumentException("Format must be pem or der.")
        };
    }

    public async Task<IssuingAuthorityPaths> GetDefaultIssuingAsync(CancellationToken ct)
    {
        var all = await ReadAsync(ct);
        var issuer = all.FirstOrDefault(x => x.Type == "intermediate" && x.IsDefaultIssuing && x.IsActive && !x.IsRevoked)
            ?? all.FirstOrDefault(x => x.Type == "intermediate" && x.IsActive && !x.IsRevoked)
            ?? throw new InvalidOperationException("Create and activate an intermediate CA before issuing certificates.");
        var parent = all.SingleOrDefault(x => x.Id == issuer.ParentId) ?? throw new InvalidOperationException("The issuing CA has no parent CA.");
        return new IssuingAuthorityPaths(issuer.Id, PathFor(issuer.Id), PathFor(parent.Id), issuer.CrlValidityDays);
    }

    public async Task<IssuingAuthorityPaths> GetIssuingAsync(string id, CancellationToken ct)
    {
        var all = await ReadAsync(ct);
        var issuer = all.SingleOrDefault(x => x.Id == id && x.Type == "intermediate" && !x.IsRevoked)
            ?? throw new InvalidOperationException("The issuing CA is unavailable.");
        var parent = all.SingleOrDefault(x => x.Id == issuer.ParentId) ?? throw new InvalidOperationException("The issuing CA has no parent CA.");
        return new IssuingAuthorityPaths(issuer.Id, PathFor(issuer.Id), PathFor(parent.Id), issuer.CrlValidityDays);
    }

    /// <summary>Returns an authority capable of signing its own CRL, including inactive issuers.
    /// Inactive issuers may still have valid certificates whose CRL must remain current.</summary>
    public async Task<CrlAuthorityPaths> GetCrlAuthorityAsync(string id, CancellationToken ct)
    {
        var authority = (await ReadAsync(ct)).SingleOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException("The certificate authority is unavailable.");
        return new CrlAuthorityPaths(authority.Id, authority.Type, PathFor(authority.Id), authority.CrlValidityDays);
    }

    /// <summary>Returns the revoked intermediate certificates directly issued by a root CA.</summary>
    public async Task<IReadOnlyList<RevokedIntermediateCertificate>> GetRevokedIntermediatesAsync(string rootId, CancellationToken ct)
    {
        var all = await ReadAsync(ct);
        return all.Where(x => x.Type == "intermediate" && x.ParentId == rootId && x.IsRevoked)
            .Select(x =>
            {
                using var certificate = Load(x);
                return new RevokedIntermediateCertificate(certificate.SerialNumber, x.RevokedAt ?? x.CreatedAt);
            })
            .ToList();
    }

    public async Task<string?> FindIssuingIdBySubjectAsync(string subject, CancellationToken ct) =>
        (await ReadAsync(ct)).SingleOrDefault(x => x.Type == "intermediate" && x.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>Returns the active root CA certificate (public part only) for trust distribution. No authentication required.</summary>
    public async Task<AuthorityCertificateExport?> GetTrustAnchorAsync(string format, CancellationToken ct)
    {
        var all = await ReadAsync(ct);
        var root = all.FirstOrDefault(x => x.Type == "root" && x.IsActive && !x.IsRevoked);
        if (root is null) return null;
        using var certificate = Load(root);
        return format.ToLowerInvariant() switch
        {
            "pem" => new AuthorityCertificateExport("homeca-root-ca.pem", "application/x-pem-file", Encoding.UTF8.GetBytes(certificate.ExportCertificatePem())),
            "der" => new AuthorityCertificateExport("homeca-root-ca.cer", "application/pkix-cert", certificate.Export(X509ContentType.Cert)),
            _ => new AuthorityCertificateExport("homeca-root-ca.pem", "application/x-pem-file", Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()))
        };
    }

    /// <summary>Returns the active issuing intermediate CA certificate (public part only) for trust distribution.</summary>
    public async Task<AuthorityCertificateExport?> GetTrustIntermediateAsync(string format, CancellationToken ct)
    {
        var all = await ReadAsync(ct);
        var intermediate = all.FirstOrDefault(x => x.Type == "intermediate" && x.IsDefaultIssuing && x.IsActive && !x.IsRevoked)
            ?? all.FirstOrDefault(x => x.Type == "intermediate" && x.IsActive && !x.IsRevoked);
        if (intermediate is null) return null;
        using var certificate = Load(intermediate);
        return format.ToLowerInvariant() switch
        {
            "pem" => new AuthorityCertificateExport("homeca-issuing-ca.pem", "application/x-pem-file", Encoding.UTF8.GetBytes(certificate.ExportCertificatePem())),
            "der" => new AuthorityCertificateExport("homeca-issuing-ca.cer", "application/pkix-cert", certificate.Export(X509ContentType.Cert)),
            _ => new AuthorityCertificateExport("homeca-issuing-ca.pem", "application/x-pem-file", Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()))
        };
    }

    /// <summary>Returns trust anchor metadata including fingerprint and subject for verification.</summary>
    public async Task<TrustAnchorInfo?> GetTrustAnchorInfoAsync(CancellationToken ct)
    {
        var all = await ReadAsync(ct);
        var root = all.FirstOrDefault(x => x.Type == "root" && x.IsActive && !x.IsRevoked);
        if (root is null) return null;
        using var certificate = Load(root);
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        return new TrustAnchorInfo(root.Subject, sha256, certificate.NotBefore, certificate.NotAfter);
    }

    public async Task<AuthorityInventory> InitializeAsync(CancellationToken ct)
    {
        if ((await ReadAsync(ct)).Count > 0) throw new InvalidOperationException("Certificate authorities are already configured.");
        var root = await CreateAsync(new("Root CA", "CN=HomeCA Root CA", "root", null, 3650, "ECC", 30), ct);
        var issuing = await CreateAsync(new("TLS Issuing CA", "CN=HomeCA TLS Issuing CA", "intermediate", root.Id, 1825, "ECC", 7), ct, true);
        return new(root.Subject, root.ExpiresAt, issuing.Subject, issuing.ExpiresAt, "ssh-host", "ssh-user");
    }

    public async Task<CertificateAuthorityInventoryItem> CreateAsync(CreateAuthorityRequest request, CancellationToken ct) =>
        await CreateAsync(request, ct, false);

    private async Task<CertificateAuthorityInventoryItem> CreateAsync(CreateAuthorityRequest request, CancellationToken ct, bool isDefaultIssuing)
    {
        Validate(request);
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct);
            if (all.Any(x => x.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("A certificate authority with this name already exists.");
            AuthorityState? parent = null;
            if (request.Type == "intermediate")
            {
                parent = all.SingleOrDefault(x => x.Id == request.ParentId) ?? throw new ArgumentException("The selected parent CA does not exist.");
                if (!parent.IsActive || parent.IsRevoked) throw new InvalidOperationException("The selected parent CA is not active.");
                using var parentCertificateForValidation = Load(parent);
                if (parentCertificateForValidation.NotAfter <= DateTime.UtcNow.AddDays(request.ValidityDays))
                    throw new ArgumentException("The intermediate CA must expire before its parent CA.");
            }
            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(_root, id));
            using var parentCertificate = parent is null ? null : Load(parent);
            using var certificate = CreateCertificate(request, parentCertificate, parent?.Id);
            await File.WriteAllBytesAsync(PathFor(id), certificate.Export(X509ContentType.Pkcs12, storage.GetCaPfxPassword()), ct);
            var item = new AuthorityState(id, request.Name.Trim(), request.Type, request.Subject.Trim(), request.ParentId, request.ValidityDays, request.KeyAlgorithm, request.CrlValidityDays, true, false, DateTimeOffset.UtcNow, certificate.NotAfter, isDefaultIssuing);
            all.Add(item); await WriteAsync(all, ct);
            logger.LogInformation("Created {Type} certificate authority {AuthorityId}", request.Type, id);
            return ToItem(item);
        }
        finally { _gate.Release(); }
    }

    public async Task<IntermediateRotationResult> RotateIntermediateAsync(RotateIntermediateRequest request, CancellationToken ct)
    {
        Validate(new CreateAuthorityRequest(request.Name, request.Subject, "intermediate", request.ParentId, request.ValidityDays, request.KeyAlgorithm, request.CrlValidityDays));
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct);
            var current = all.FirstOrDefault(x => x.Type == "intermediate" && x.IsDefaultIssuing && x.IsActive && !x.IsRevoked)
                ?? all.FirstOrDefault(x => x.Type == "intermediate" && x.IsActive && !x.IsRevoked)
                ?? throw new InvalidOperationException("No active issuing CA is available for rotation.");
            var parent = all.SingleOrDefault(x => x.Id == request.ParentId) ?? throw new ArgumentException("The selected parent CA does not exist.");
            if (parent.Type != "root" || !parent.IsActive || parent.IsRevoked) throw new InvalidOperationException("The selected parent root CA is not active.");
            if (all.Any(x => x.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("A certificate authority with this name already exists.");
            using var parentCertificate = Load(parent);
            if (parentCertificate.NotAfter <= DateTime.UtcNow.AddDays(request.ValidityDays)) throw new ArgumentException("The replacement intermediate CA must expire before its parent root CA.");

            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(_root, id));
            using var certificate = CreateCertificate(new(request.Name, request.Subject, "intermediate", request.ParentId, request.ValidityDays, request.KeyAlgorithm, request.CrlValidityDays), parentCertificate, request.ParentId);
            await File.WriteAllBytesAsync(PathFor(id), certificate.Export(X509ContentType.Pkcs12, storage.GetCaPfxPassword()), ct);
            var replacement = new AuthorityState(id, request.Name.Trim(), "intermediate", request.Subject.Trim(), request.ParentId, request.ValidityDays, request.KeyAlgorithm, request.CrlValidityDays, true, false, DateTimeOffset.UtcNow, certificate.NotAfter, true);
            var currentIndex = all.FindIndex(x => x.Id == current.Id);
            all[currentIndex] = current with { IsActive = false, IsDefaultIssuing = false };
            all.Add(replacement);
            await WriteAsync(all, ct);
            var dependent = GetIssuedCertificateExpiry(current);
            logger.LogInformation("Rotated TLS issuing CA from {PreviousAuthorityId} to {ReplacementAuthorityId}", current.Id, replacement.Id);
            return new IntermediateRotationResult(ToItem(current with { IsActive = false, IsDefaultIssuing = false }), ToItem(replacement), dependent.Count, dependent.LatestExpiry);
        }
        finally { _gate.Release(); }
    }

    public async Task<CertificateAuthorityInventoryItem?> UpdateAsync(string id, UpdateAuthorityRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct); var index = all.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            if (string.IsNullOrWhiteSpace(request.Name) || request.CrlValidityDays is < 1 or > 365) throw new ArgumentException("Name and CRL validity are invalid.");
            var item = all[index];
            if (item.IsDefaultIssuing && !request.IsActive)
                throw new InvalidOperationException("Rotate the active issuing CA before deactivating it.");
            if (item.IsRevoked && request.IsActive) throw new InvalidOperationException("A revoked CA cannot be reactivated. Create a replacement CA instead.");
            all[index] = item with { Name = request.Name.Trim(), IsActive = request.IsActive, CrlValidityDays = request.CrlValidityDays };
            await WriteAsync(all, ct); return ToItem(all[index]);
        }
        finally { _gate.Release(); }
    }

    public async Task<CertificateAuthorityInventoryItem?> RevokeAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct); var index = all.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            if (all.Any(x => x.ParentId == id && !x.IsRevoked)) throw new InvalidOperationException("Revoke or delete subordinate CAs first.");
            all[index] = all[index] with { IsActive = false, IsRevoked = true, RevokedAt = DateTimeOffset.UtcNow };
            await WriteAsync(all, ct); return ToItem(all[index]);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct); var item = all.SingleOrDefault(x => x.Id == id);
            if (item is null) return false;
            if (all.Any(x => x.ParentId == id)) throw new InvalidOperationException("Delete subordinate CAs first.");
            if (item.IsActive && !item.IsRevoked) throw new InvalidOperationException("Deactivate or revoke the CA before deleting it.");
            if (HasIssuedCertificates(item)) throw new InvalidOperationException("The CA has issued certificates and cannot be deleted. Keep it revoked for audit and CRL purposes.");
            var directory = Path.Combine(_root, id); if (Directory.Exists(directory)) Directory.Delete(directory, true);
            all.Remove(item); await WriteAsync(all, ct); return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<AuthorityState>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_statePath))
        {
            var legacyRoot = Path.Combine(_root, "root", "root-ca.pfx");
            var legacyIssuer = Path.Combine(_root, "tls-issuing", "tls-issuing-ca.pfx");
            if (!File.Exists(legacyRoot) || !File.Exists(legacyIssuer)) return [];
            using var root = X509CertificateLoader.LoadPkcs12FromFile(legacyRoot, null);
            using var issuer = X509CertificateLoader.LoadPkcs12FromFile(legacyIssuer, null);
            Directory.CreateDirectory(Path.Combine(_root, "root")); Directory.CreateDirectory(Path.Combine(_root, "tls-issuing"));
            File.WriteAllBytes(PathFor("root"), root.Export(X509ContentType.Pkcs12, storage.GetCaPfxPassword()));
            File.WriteAllBytes(PathFor("tls-issuing"), issuer.Export(X509ContentType.Pkcs12, storage.GetCaPfxPassword()));
            var migrated = new List<AuthorityState> { new("root", "Root CA", "root", root.Subject, null, (int)(root.NotAfter - DateTime.UtcNow).TotalDays, "ECC", 30, true, false, DateTimeOffset.UtcNow, root.NotAfter), new("tls-issuing", "TLS Issuing CA", "intermediate", issuer.Subject, "root", (int)(issuer.NotAfter - DateTime.UtcNow).TotalDays, "ECC", 7, true, false, DateTimeOffset.UtcNow, issuer.NotAfter, true) };
            await WriteAsync(migrated, ct); return migrated;
        }
        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<List<AuthorityState>>(stream, cancellationToken: ct) ?? [];
    }
    private async Task WriteAsync(List<AuthorityState> all, CancellationToken ct)
    {
        var temporaryPath = _statePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, all, cancellationToken: ct);
        File.Move(temporaryPath, _statePath, true);
    }
    public X509Certificate2 LoadAuthorityCertificate(string authorityPath)
    {
        var password = storage.GetCaPfxPassword();
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(authorityPath, password);
        }
        catch (CryptographicException)
        {
            // Upgrade legacy, passwordless CA PFX files in place once the CA key is configured.
            using var legacy = X509CertificateLoader.LoadPkcs12FromFile(authorityPath, null);
            if (!legacy.HasPrivateKey) throw;
            File.WriteAllBytes(authorityPath, legacy.Export(X509ContentType.Pkcs12, password));
            logger.LogWarning("Migrated legacy passwordless CA PFX at {AuthorityPath} to encrypted storage", authorityPath);
            return X509CertificateLoader.LoadPkcs12FromFile(authorityPath, password);
        }
    }

    private X509Certificate2 Load(AuthorityState state) => LoadAuthorityCertificate(PathFor(state.Id));
    private bool HasIssuedCertificates(AuthorityState authority)
    {
        var certificates = Path.Combine(storage.RootPath, "certificates");
        if (!Directory.Exists(certificates)) return false;
        return Directory.EnumerateFiles(certificates, "certificate.pfx", SearchOption.AllDirectories).Any(path =>
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            return certificate.Issuer.Equals(authority.Subject, StringComparison.OrdinalIgnoreCase);
        });
    }
    private (int Count, DateTime? LatestExpiry) GetIssuedCertificateExpiry(AuthorityState authority)
    {
        var certificates = Path.Combine(storage.RootPath, "certificates");
        if (!Directory.Exists(certificates)) return (0, null);
        var matching = new List<DateTime>();
        foreach (var path in Directory.EnumerateFiles(certificates, "certificate.pfx", SearchOption.AllDirectories))
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            if (certificate.Issuer.Equals(authority.Subject, StringComparison.OrdinalIgnoreCase)) matching.Add(certificate.NotAfter);
        }
        return (matching.Count, matching.Count == 0 ? null : matching.Max());
    }
    private string PathFor(string id) => Path.Combine(_root, id, "authority.pfx");
    private static CertificateAuthorityInventoryItem ToItem(AuthorityState x) => new(x.Id, x.Name, x.Type, x.Subject, x.ExpiresAt, x.ParentId, x.IsActive, x.IsRevoked, x.CrlValidityDays, x.IsDefaultIssuing);
    private static void Validate(CreateAuthorityRequest x)
    {
        if (string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Subject) || x.Type is not ("root" or "intermediate") || x.KeyAlgorithm is not ("ECC" or "RSA")) throw new ArgumentException("Name, subject, type and key algorithm are invalid.");
        if (x.ValidityDays is < 1 or > 7300 || x.CrlValidityDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(x.ValidityDays));
    }
    private X509Certificate2 CreateCertificate(CreateAuthorityRequest x, X509Certificate2? parent, string? parentId)
    {
        using var ecc = x.KeyAlgorithm == "ECC" ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        using var rsa = x.KeyAlgorithm == "RSA" ? RSA.Create(3072) : null;
        var request = ecc is not null ? new CertificateRequest(x.Subject, ecc, HashAlgorithmName.SHA384) : new CertificateRequest(new X500DistinguishedName(x.Subject), rsa!, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, parent is null, parent is null ? 1 : 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var publicUrl = storage.PublicUrl?.TrimEnd('/');
        if (parent is not null && parentId is not null && !string.IsNullOrWhiteSpace(publicUrl))
            request.CertificateExtensions.Add(BuildCdpExtension($"{publicUrl}/api/v1/crl/{parentId}"));
        var from = DateTimeOffset.UtcNow.AddMinutes(-5); var until = DateTimeOffset.UtcNow.AddDays(x.ValidityDays);
        if (parent is null) return request.CreateSelfSigned(from, until);
        using var issued = request.Create(parent, from, until, RandomNumberGenerator.GetBytes(16));
        return ecc is not null ? issued.CopyWithPrivateKey(ecc) : issued.CopyWithPrivateKey(rsa!);
    }
    private static X509Extension BuildCdpExtension(string url)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            writer.WriteCharacterString(UniversalTagNumber.IA5String, url, new Asn1Tag(TagClass.ContextSpecific, 6));
        }
        return new X509Extension("2.5.29.31", writer.Encode(), false);
    }
    private sealed record AuthorityState(string Id, string Name, string Type, string Subject, string? ParentId, int ValidityDays, string KeyAlgorithm, int CrlValidityDays, bool IsActive, bool IsRevoked, DateTimeOffset CreatedAt, DateTime ExpiresAt, bool IsDefaultIssuing = false, DateTimeOffset? RevokedAt = null);
}

public sealed record CreateAuthorityRequest(string Name, string Subject, string Type, string? ParentId, int ValidityDays, string KeyAlgorithm, int CrlValidityDays);
public sealed record UpdateAuthorityRequest(string Name, bool IsActive, int CrlValidityDays);
public sealed record RotateIntermediateRequest(string Name, string Subject, string ParentId, int ValidityDays, string KeyAlgorithm, int CrlValidityDays);
public sealed record IntermediateRotationResult(CertificateAuthorityInventoryItem PreviousAuthority, CertificateAuthorityInventoryItem ReplacementAuthority, int DependentCertificateCount, DateTime? LatestDependentCertificateExpiry);
public sealed record IssuingAuthorityPaths(string Id, string IssuingPath, string RootPath, int CrlValidityDays);
public sealed record CrlAuthorityPaths(string Id, string Type, string AuthorityPath, int CrlValidityDays);
public sealed record RevokedIntermediateCertificate(string SerialNumber, DateTimeOffset RevokedAt);
public sealed record AuthorityCertificateExport(string FileName, string ContentType, byte[] Content);
public sealed record AuthorityInventory(string RootSubject, DateTime RootExpiresAt, string TlsIssuingSubject, DateTime TlsIssuingExpiresAt, string SshHostAuthority, string SshUserAuthority);
public sealed record CertificateAuthorityInventoryItem(string Id, string Name, string Type, string Subject, DateTime ExpiresAt, string? ParentId, bool IsActive, bool IsRevoked, int CrlValidityDays, bool IsDefaultIssuing = false);
public sealed record TrustAnchorInfo(string Subject, string Sha256Fingerprint, DateTime ValidFrom, DateTime ExpiresAt);
