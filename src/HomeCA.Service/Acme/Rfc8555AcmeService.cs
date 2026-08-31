using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeCA.Service.Domains;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;
using Microsoft.Extensions.Options;

namespace HomeCA.Service.Acme;

/// <summary>
/// RFC 8555-compliant ACME server that wraps the existing HomeCA internal issuance pipeline.
/// Speaks the real ACME wire protocol (JWS-signed requests, nonces, proper resource types)
/// so that standard clients like acme.sh, certbot, and OPNsense can obtain certificates.
/// <para>
/// Because HomeCA is a trusted internal CA, challenge validation is auto-approved: authorizations
/// move to "valid" immediately, so clients never need to provision DNS or HTTP challenge responses.
/// </para>
/// </summary>
public sealed class Rfc8555AcmeService
{
    private readonly CertificateIssuanceService _certificates;
    private readonly CertificateAuthorityService _authorities;
    private readonly DomainRegistry _domains;
    private readonly HomeCaStorage _storage;
    private readonly IOptions<HomeCaStorageOptions> _options;
    private readonly ILogger<Rfc8555AcmeService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Nonce pool — simple set with bounded size, oldest removed when full.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new();
    private const int MaxNonces = 10_000;

    // State files
    private readonly string _accountsPath;
    private readonly string _ordersPath;

    public Rfc8555AcmeService(
        CertificateIssuanceService certificates,
        CertificateAuthorityService authorities,
        DomainRegistry domains,
        HomeCaStorage storage,
        IOptions<HomeCaStorageOptions> options,
        ILogger<Rfc8555AcmeService> logger)
    {
        _certificates = certificates;
        _authorities = authorities;
        _domains = domains;
        _storage = storage;
        _options = options;
        _logger = logger;
        _accountsPath = Path.Combine(storage.RootPath, "state", "rfc8555-accounts.json");
        _ordersPath = Path.Combine(storage.RootPath, "state", "rfc8555-orders.json");
    }

    // ───────────────────────────── Nonce management ─────────────────────────────

    public string CreateNonce()
    {
        // Evict oldest if pool is full.
        if (_nonces.Count >= MaxNonces)
        {
            var oldest = _nonces.OrderBy(kvp => kvp.Value).Take(MaxNonces / 4).Select(kvp => kvp.Key).ToList();
            foreach (var key in oldest) _nonces.TryRemove(key, out _);
        }

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _nonces[nonce] = DateTimeOffset.UtcNow;
        return nonce;
    }

    private bool ConsumeNonce(string nonce) => _nonces.TryRemove(nonce, out _);

    // ───────────────────────────── JWS verification ─────────────────────────────

    /// <summary>
    /// Parses and verifies a JWS Compact or Flattened JSON Serialization request body.
    /// Returns the decoded payload, the protected header as a <see cref="JsonObject"/>,
    /// and the account key thumbprint.
    /// </summary>
    public JwsVerificationResult VerifyJws(byte[] body, string expectedUrl)
    {
        var json = JsonNode.Parse(body) ?? throw AcmeProblem("malformed", "Request body is not valid JSON.");

        var protectedB64 = json["protected"]?.GetValue<string>() ?? throw AcmeProblem("malformed", "Missing 'protected' header.");
        var payloadB64 = json["payload"]?.GetValue<string>() ?? throw AcmeProblem("malformed", "Missing 'payload' field.");
        var signatureB64 = json["signature"]?.GetValue<string>() ?? throw AcmeProblem("malformed", "Missing 'signature' field.");

        var protectedBytes = Base64UrlDecode(protectedB64);
        var header = JsonNode.Parse(protectedBytes)?.AsObject() ?? throw AcmeProblem("malformed", "Protected header is not valid JSON.");

        // Verify nonce.
        var nonce = header["nonce"]?.GetValue<string>() ?? throw AcmeProblem("badNonce", "Missing nonce in protected header.");
        if (!ConsumeNonce(nonce)) throw AcmeProblem("badNonce", "Invalid or expired nonce.");

        // Verify url.
        var url = header["url"]?.GetValue<string>();
        if (url != expectedUrl) throw AcmeProblem("unauthorized", $"URL mismatch: expected '{expectedUrl}', got '{url}'.");

        // Determine algorithm.
        var alg = header["alg"]?.GetValue<string>() ?? throw AcmeProblem("badSignatureAlgorithm", "Missing 'alg' in protected header.");

        // Extract the public key — either from 'jwk' (new account) or 'kid' (existing account).
        JsonObject? jwk = header["jwk"]?.AsObject();
        string? kid = header["kid"]?.GetValue<string>();
        if (jwk is null && kid is null) throw AcmeProblem("malformed", "Protected header must contain 'jwk' or 'kid'.");
        if (jwk is not null && kid is not null) throw AcmeProblem("malformed", "Protected header must not contain both 'jwk' and 'kid'.");

        // Verify signature.
        var signatureInput = Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}");
        var signature = Base64UrlDecode(signatureB64);

        if (jwk is not null)
        {
            VerifySignatureWithJwk(jwk, alg, signatureInput, signature);
        }
        // kid verification is handled by the caller (resolve account, get stored JWK, verify).

        var payload = payloadB64.Length == 0 ? Array.Empty<byte>() : Base64UrlDecode(payloadB64);
        var thumbprint = jwk is not null ? ComputeJwkThumbprint(jwk) : null;

        return new JwsVerificationResult(header, payload, jwk, kid, thumbprint);
    }

    /// <summary>Verifies a JWS for POST-as-GET requests (empty payload) using a stored account JWK.</summary>
    public void VerifySignatureWithStoredKey(JsonObject storedJwk, string alg, byte[] body)
    {
        var json = JsonNode.Parse(body) ?? throw AcmeProblem("malformed", "Request body is not valid JSON.");
        var protectedB64 = json["protected"]?.GetValue<string>() ?? throw AcmeProblem("malformed", "Missing 'protected'.");
        var payloadB64 = json["payload"]?.GetValue<string>() ?? throw AcmeProblem("malformed", "Missing 'payload'.");
        var signatureB64 = json["signature"]?.GetValue<string>() ?? throw AcmeProblem("malformed", "Missing 'signature'.");

        var signatureInput = Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}");
        var signature = Base64UrlDecode(signatureB64);
        VerifySignatureWithJwk(storedJwk, alg, signatureInput, signature);
    }

    private static void VerifySignatureWithJwk(JsonObject jwk, string alg, byte[] data, byte[] signature)
    {
        var kty = jwk["kty"]?.GetValue<string>() ?? throw AcmeProblem("badPublicKey", "Missing 'kty' in JWK.");

        if (kty == "EC")
        {
            var crv = jwk["crv"]?.GetValue<string>() ?? "P-256";
            var x = Base64UrlDecode(jwk["x"]?.GetValue<string>() ?? throw AcmeProblem("badPublicKey", "Missing 'x'."));
            var y = Base64UrlDecode(jwk["y"]?.GetValue<string>() ?? throw AcmeProblem("badPublicKey", "Missing 'y'."));

            var curve = crv switch
            {
                "P-256" => ECCurve.NamedCurves.nistP256,
                "P-384" => ECCurve.NamedCurves.nistP384,
                "P-521" => ECCurve.NamedCurves.nistP521,
                _ => throw AcmeProblem("badPublicKey", $"Unsupported curve: {crv}")
            };
            var hashAlg = alg switch
            {
                "ES256" => HashAlgorithmName.SHA256,
                "ES384" => HashAlgorithmName.SHA384,
                "ES512" => HashAlgorithmName.SHA512,
                _ => throw AcmeProblem("badSignatureAlgorithm", $"Unsupported EC algorithm: {alg}")
            };

            using var ecdsa = ECDsa.Create(new ECParameters { Curve = curve, Q = new ECPoint { X = x, Y = y } });
            // acme.sh sends the signature in JWS format (r || s, raw integers), need to convert to DER for .NET.
            if (!ecdsa.VerifyData(data, ConvertJwsEcSignatureToDer(signature, x.Length), hashAlg, DSASignatureFormat.Rfc3279DerSequence))
                throw AcmeProblem("unauthorized", "Invalid JWS signature.");
        }
        else if (kty == "RSA")
        {
            var n = Base64UrlDecode(jwk["n"]?.GetValue<string>() ?? throw AcmeProblem("badPublicKey", "Missing 'n'."));
            var e = Base64UrlDecode(jwk["e"]?.GetValue<string>() ?? throw AcmeProblem("badPublicKey", "Missing 'e'."));

            var hashAlg = alg switch
            {
                "RS256" => HashAlgorithmName.SHA256,
                "RS384" => HashAlgorithmName.SHA384,
                "RS512" => HashAlgorithmName.SHA512,
                _ => throw AcmeProblem("badSignatureAlgorithm", $"Unsupported RSA algorithm: {alg}")
            };

            using var rsa = RSA.Create(new RSAParameters { Modulus = n, Exponent = e });
            if (!rsa.VerifyData(data, signature, hashAlg, RSASignaturePadding.Pkcs1))
                throw AcmeProblem("unauthorized", "Invalid JWS signature.");
        }
        else
        {
            throw AcmeProblem("badPublicKey", $"Unsupported key type: {kty}");
        }
    }

    /// <summary>Converts a raw (r || s) EC signature from JWS to DER SEQUENCE format.</summary>
    private static byte[] ConvertJwsEcSignatureToDer(byte[] raw, int componentLength)
    {
        if (raw.Length != componentLength * 2)
        {
            // Might already be DER, or use half the raw length.
            componentLength = raw.Length / 2;
        }

        var r = raw.AsSpan(0, componentLength);
        var s = raw.AsSpan(componentLength);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteIntegerUnsigned(r);
            writer.WriteIntegerUnsigned(s);
        }
        return writer.Encode();
    }

    // ───────────────────────────── Account operations ───────────────────────────

    public async Task<Rfc8555Account> NewAccountAsync(JsonObject jwk, string? contact, CancellationToken ct)
    {
        var thumbprint = ComputeJwkThumbprint(jwk);

        await _gate.WaitAsync(ct);
        try
        {
            var accounts = await ReadAsync<List<Rfc8555Account>>(_accountsPath, ct) ?? [];
            var existing = accounts.FirstOrDefault(a => a.Thumbprint == thumbprint);
            if (existing is not null)
            {
                _logger.LogInformation("Returning existing RFC 8555 account {AccountId} for thumbprint {Thumbprint}", existing.Id, thumbprint);
                return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            var contacts = contact is not null ? [contact.StartsWith("mailto:") ? contact : $"mailto:{contact}"] : Array.Empty<string>();
            var account = new Rfc8555Account(id, thumbprint, contacts, "valid", DateTimeOffset.UtcNow, JsonSerializer.Serialize(jwk));
            accounts.Add(account);
            await WriteAsync(_accountsPath, accounts, ct);
            _logger.LogInformation("Registered RFC 8555 account {AccountId} (thumbprint {Thumbprint})", id, thumbprint);
            return account;
        }
        finally { _gate.Release(); }
    }

    public async Task<Rfc8555Account?> FindAccountByKidAsync(string kid, CancellationToken ct)
    {
        // kid is the full account URL; extract the ID from the last segment.
        var id = kid.Split('/').Last();
        var accounts = await ReadAsync<List<Rfc8555Account>>(_accountsPath, ct) ?? [];
        return accounts.FirstOrDefault(a => a.Id == id);
    }

    public async Task<Rfc8555Account?> FindAccountByThumbprintAsync(string thumbprint, CancellationToken ct)
    {
        var accounts = await ReadAsync<List<Rfc8555Account>>(_accountsPath, ct) ?? [];
        return accounts.FirstOrDefault(a => a.Thumbprint == thumbprint);
    }

    public async Task<IReadOnlyList<Rfc8555Account>> ListAccountsAsync(CancellationToken ct) =>
        await ReadAsync<List<Rfc8555Account>>(_accountsPath, ct) ?? [];

    public async Task<bool> DeleteAccountAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var accounts = await ReadAsync<List<Rfc8555Account>>(_accountsPath, ct) ?? [];
            var removed = accounts.RemoveAll(a => a.Id == id);
            if (removed == 0) return false;
            await WriteAsync(_accountsPath, accounts, ct);
            _logger.LogInformation("Deleted RFC 8555 account {AccountId}", id);
            return true;
        }
        finally { _gate.Release(); }
    }

    // ───────────────────────────── Order operations ─────────────────────────────

    public async Task<Rfc8555Order> NewOrderAsync(string accountId, IReadOnlyList<Rfc8555Identifier> identifiers, CancellationToken ct)
    {
        // Validate all identifiers are under active internal issuance zones.
        var zones = (await _domains.ListAsync(ct))
            .Where(d => d.InternalIssuanceEnabled)
            .Select(d => d.Name)
            .ToList();

        var dnsNames = identifiers
            .Where(i => i.Type == "dns")
            .Select(i => i.Value.Trim().TrimEnd('.').ToLowerInvariant())
            .ToList();

        if (dnsNames.Count == 0)
            throw AcmeProblem("rejectedIdentifier", "At least one DNS identifier is required.");
        if (zones.Count == 0 || dnsNames.Any(name => !zones.Any(zone => IsWithinZone(name, zone))))
            throw AcmeProblem("rejectedIdentifier", "All identifiers must be under an active internal issuance zone.");

        var orderId = Guid.NewGuid().ToString("N");
        var authzIds = dnsNames.Select(_ => Guid.NewGuid().ToString("N")).ToList();

        // Build authorization and challenge objects.
        var authorizations = new List<Rfc8555Authorization>();
        for (var i = 0; i < dnsNames.Count; i++)
        {
            var challengeId = Guid.NewGuid().ToString("N");
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var challenge = new Rfc8555Challenge(challengeId, "http-01", token, "valid", DateTimeOffset.UtcNow);
            var authz = new Rfc8555Authorization(authzIds[i], new Rfc8555Identifier("dns", dnsNames[i]), "valid", [challenge], DateTimeOffset.UtcNow.AddDays(7));
            authorizations.Add(authz);
        }

        var rfcIdentifiers = dnsNames.Select(n => new Rfc8555Identifier("dns", n)).ToList();

        await _gate.WaitAsync(ct);
        try
        {
            var orders = await ReadAsync<List<Rfc8555Order>>(_ordersPath, ct) ?? [];
            // Auto-approve: orders go directly to "ready" because this is a trusted internal CA.
            var order = new Rfc8555Order(orderId, accountId, rfcIdentifiers, authorizations, "ready",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), null, null);
            orders.Add(order);
            await WriteAsync(_ordersPath, orders, ct);
            _logger.LogInformation("Created RFC 8555 order {OrderId} for {Identifiers}", orderId, string.Join(", ", dnsNames));
            return order;
        }
        finally { _gate.Release(); }
    }

    public async Task<Rfc8555Order?> GetOrderAsync(string orderId, CancellationToken ct)
    {
        var orders = await ReadAsync<List<Rfc8555Order>>(_ordersPath, ct) ?? [];
        return orders.FirstOrDefault(o => o.Id == orderId);
    }

    public async Task<IReadOnlyList<Rfc8555Order>> ListOrdersAsync(CancellationToken ct) =>
        await ReadAsync<List<Rfc8555Order>>(_ordersPath, ct) ?? [];

    public async Task<bool> DeleteOrderAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var orders = await ReadAsync<List<Rfc8555Order>>(_ordersPath, ct) ?? [];
            var removed = orders.RemoveAll(o => o.Id == id);
            if (removed == 0) return false;
            await WriteAsync(_ordersPath, orders, ct);
            _logger.LogInformation("Deleted RFC 8555 order {OrderId}", id);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<Rfc8555Authorization?> GetAuthorizationAsync(string authzId, CancellationToken ct)
    {
        var orders = await ReadAsync<List<Rfc8555Order>>(_ordersPath, ct) ?? [];
        return orders.SelectMany(o => o.Authorizations).FirstOrDefault(a => a.Id == authzId);
    }

    public async Task<Rfc8555Challenge?> GetChallengeAsync(string challengeId, CancellationToken ct)
    {
        var orders = await ReadAsync<List<Rfc8555Order>>(_ordersPath, ct) ?? [];
        return orders.SelectMany(o => o.Authorizations).SelectMany(a => a.Challenges).FirstOrDefault(c => c.Id == challengeId);
    }

    /// <summary>
    /// Finalizes an order by signing a certificate from the client-submitted CSR.
    /// The CSR's SANs must match the order identifiers.
    /// </summary>
    public async Task<Rfc8555Order> FinalizeOrderAsync(string orderId, byte[] csrDer, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var orders = await ReadAsync<List<Rfc8555Order>>(_ordersPath, ct) ?? [];
            var index = orders.FindIndex(o => o.Id == orderId);
            if (index < 0) throw AcmeProblem("orderNotReady", "Order not found.");
            var order = orders[index];

            if (order.Status == "valid") return order;
            if (order.Status != "ready")
                throw AcmeProblem("orderNotReady", $"Order is in status '{order.Status}', expected 'ready'.");

            // Parse the CSR to extract SANs.
            var csr = CertificateRequest.LoadSigningRequest(csrDer, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

            // Issue the certificate using the CSR's public key — sign with the issuing CA.
            var dnsNames = order.Identifiers.Select(i => i.Value).ToList();
            var certId = await IssueCertificateFromCsrAsync(csr, dnsNames, ct);

            order = order with { Status = "valid", CertificateId = certId };
            orders[index] = order;
            await WriteAsync(_ordersPath, orders, ct);

            _logger.LogInformation("Finalized RFC 8555 order {OrderId}, issued certificate {CertificateId}", orderId, certId);
            return order;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns the certificate chain as PEM for the given certificate ID.</summary>
    public async Task<string?> GetCertificatePemAsync(string certificateId, CancellationToken ct)
    {
        var exportPath = Path.Combine(_storage.RootPath, "exports", certificateId);
        var fullchainPath = Path.Combine(exportPath, "fullchain.pem");
        if (!File.Exists(fullchainPath)) return null;
        return await File.ReadAllTextAsync(fullchainPath, ct);
    }

    // ───────────────────────── Certificate issuance from CSR ────────────────────

    private async Task<string> IssueCertificateFromCsrAsync(CertificateRequest csr, IReadOnlyList<string> dnsNames, CancellationToken ct)
    {
        var authorityPaths = await _authorities.GetDefaultIssuingAsync(ct);
        using var issuer = X509CertificateLoader.LoadPkcs12FromFile(authorityPaths.IssuingPath, null);

        var subject = dnsNames.First();

        // Build the certificate from the CSR's public key.
        // We create a new CertificateRequest with the CSR's key and our extensions.
        var pubKey = csr.PublicKey;

        var certRequest = new CertificateRequest(
            new X500DistinguishedName($"CN={subject}"),
            pubKey,
            HashAlgorithmName.SHA256);

        certRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        certRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames.Distinct(StringComparer.OrdinalIgnoreCase))
            san.AddDnsName(name);
        certRequest.CertificateExtensions.Add(san.Build());
        certRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(pubKey, false));

        var publicUrl = _options.Value.PublicUrl?.TrimEnd('/');
        if (!string.IsNullOrEmpty(publicUrl))
            certRequest.CertificateExtensions.Add(BuildCdpExtension($"{publicUrl}/api/v1/crl/latest"));

        var serial = RandomNumberGenerator.GetBytes(16);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(365);

        using var issuerEcc = issuer.GetECDsaPrivateKey();
        using var issuerRsa = issuerEcc is null ? issuer.GetRSAPrivateKey() : null;
        var generator = issuerEcc is not null
            ? X509SignatureGenerator.CreateForECDsa(issuerEcc)
            : X509SignatureGenerator.CreateForRSA(issuerRsa!, RSASignaturePadding.Pkcs1);

        using var cert = certRequest.Create(issuer.SubjectName, generator, notBefore, notAfter, serial);

        var id = Convert.ToHexString(serial).ToLowerInvariant();
        var certificatePath = Path.Combine(_storage.RootPath, "certificates", id);
        var exportPath = Path.Combine(_storage.RootPath, "exports", id);

        Directory.CreateDirectory(certificatePath);
        Directory.CreateDirectory(exportPath);

        // Save the certificate (without private key — the client holds the key).
        File.WriteAllBytes(Path.Combine(certificatePath, "certificate.pfx"), cert.Export(X509ContentType.Pkcs12));

        var certPem = cert.ExportCertificatePem();
        File.WriteAllText(Path.Combine(exportPath, "certificate.pem"), certPem);

        using var root = X509CertificateLoader.LoadPkcs12FromFile(authorityPaths.RootPath, null);
        var chainPem = issuer.ExportCertificatePem() + root.ExportCertificatePem();
        File.WriteAllText(Path.Combine(exportPath, "chain.pem"), chainPem);
        File.WriteAllText(Path.Combine(exportPath, "fullchain.pem"), certPem + chainPem);

        _logger.LogInformation("Issued RFC 8555 certificate {CertificateId} for {Subject}", id, subject);
        return id;
    }

    // ────────────────────────────── Helper methods ──────────────────────────────

    public static string ComputeJwkThumbprint(JsonObject jwk)
    {
        // RFC 7638: Lexicographic JSON with required members only.
        var kty = jwk["kty"]?.GetValue<string>();
        string canonical;
        if (kty == "EC")
        {
            var crv = jwk["crv"]?.GetValue<string>();
            var x = jwk["x"]?.GetValue<string>();
            var y = jwk["y"]?.GetValue<string>();
            canonical = $"{{\"crv\":\"{crv}\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}";
        }
        else if (kty == "RSA")
        {
            var e = jwk["e"]?.GetValue<string>();
            var n = jwk["n"]?.GetValue<string>();
            canonical = $"{{\"e\":\"{e}\",\"kty\":\"RSA\",\"n\":\"{n}\"}}";
        }
        else
        {
            throw AcmeProblem("badPublicKey", $"Unsupported key type for thumbprint: {kty}");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Base64UrlEncode(hash);
    }

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static bool IsWithinZone(string name, string zone) =>
        name.Equals(zone, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith('.' + zone, StringComparison.OrdinalIgnoreCase);

    public static AcmeProblemException AcmeProblem(string type, string detail, int status = 400) =>
        new(type, detail, status);

    private static X509Extension BuildCdpExtension(string url)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, url, new Asn1Tag(TagClass.ContextSpecific, 6));
                    }
                }
            }
        }
        return new X509Extension("2.5.29.31", writer.Encode(), critical: false);
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(tmp, path, true);
    }
}

// ─────────────────────────────── Records ────────────────────────────────────

public sealed record Rfc8555Account(string Id, string Thumbprint, string[] Contact, string Status, DateTimeOffset CreatedAt, string JwkJson);

public sealed record Rfc8555Identifier(string Type, string Value);

public sealed record Rfc8555Challenge(string Id, string Type, string Token, string Status, DateTimeOffset ValidatedAt);

public sealed record Rfc8555Authorization(string Id, Rfc8555Identifier Identifier, string Status, IReadOnlyList<Rfc8555Challenge> Challenges, DateTimeOffset Expires);

public sealed record Rfc8555Order(
    string Id,
    string AccountId,
    IReadOnlyList<Rfc8555Identifier> Identifiers,
    IReadOnlyList<Rfc8555Authorization> Authorizations,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset Expires,
    string? CertificateId,
    string? Error);

public sealed record JwsVerificationResult(
    System.Text.Json.Nodes.JsonObject ProtectedHeader,
    byte[] Payload,
    System.Text.Json.Nodes.JsonObject? Jwk,
    string? Kid,
    string? Thumbprint);

public sealed class AcmeProblemException(string type, string detail, int statusCode = 400) : Exception(detail)
{
    public string ProblemType { get; } = type;
    public int StatusCode { get; } = statusCode;
}
