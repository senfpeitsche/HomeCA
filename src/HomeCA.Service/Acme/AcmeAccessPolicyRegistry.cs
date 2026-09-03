using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Acme;

/// <summary>
/// Stores RFC 8555 admission policy. Clients outside the network allowlist need
/// an individually issued EAB credential to create exactly one ACME account.
/// </summary>
public sealed class AcmeAccessPolicyRegistry(HomeCaStorage storage)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "acme-access-policy.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AcmeAccessPolicy> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAndMigrateUnsafeAsync(ct);
            if (!File.Exists(_path)) await WriteUnsafeAsync(state, ct);
            return ToPublicPolicy(state);
        }
        finally { _gate.Release(); }
    }

    public async Task<AcmeAccessPolicy> UpdateAsync(UpdateAcmeAccessPolicyRequest request, CancellationToken ct)
    {
        var networks = NormalizeNetworks(request.AllowlistedClientNetworks);
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAndMigrateUnsafeAsync(ct);
            state = state with { AllowlistedClientNetworks = networks };
            await WriteUnsafeAsync(state, ct);
            return ToPublicPolicy(state);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Creates a credential for one named ACME client. The secret is returned only here.</summary>
    public async Task<AcmeEabCredentials> CreateEabAsync(CreateAcmeEabCredentialRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An EAB credential name is required.");
        if (name.Length > 100) throw new ArgumentException("The EAB credential name must not exceed 100 characters.");

        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAndMigrateUnsafeAsync(ct);
            var credential = new AcmeEabCredentialState(Guid.NewGuid().ToString("N"), name,
                Rfc8555AcmeService.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow, null, null, null);
            await WriteUnsafeAsync(state with { EabCredentials = [.. state.EabCredentials, credential] }, ct);
            return new AcmeEabCredentials(credential.KeyId, credential.HmacKey);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RevokeEabAsync(string keyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAndMigrateUnsafeAsync(ct);
            var index = state.EabCredentials.FindIndex(credential => credential.KeyId == keyId);
            if (index < 0) return false;
            var credential = state.EabCredentials[index];
            if (credential.RevokedAt is not null) return true;
            state.EabCredentials[index] = credential with { RevokedAt = DateTimeOffset.UtcNow };
            await WriteUnsafeAsync(state, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Authorizes a new account and consumes the matching EAB credential before
    /// the account is created, preventing it from admitting a second account.
    /// </summary>
    public async Task<string?> AuthorizeNewAccountAsync(IPAddress? remoteAddress, JsonObject? externalAccountBinding, JsonObject accountJwk, string expectedUrl, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAndMigrateUnsafeAsync(ct);
            if (!File.Exists(_path)) await WriteUnsafeAsync(state, ct);
            if (IsAllowlisted(remoteAddress, state.AllowlistedClientNetworks)) return null;
            if (externalAccountBinding is null)
                throw Rfc8555AcmeService.AcmeProblem("externalAccountRequired", "This ACME client is not allowlisted and must provide externalAccountBinding.", 400);

            var keyId = VerifyEab(externalAccountBinding, accountJwk, expectedUrl, state.EabCredentials);
            var index = state.EabCredentials.FindIndex(credential => credential.KeyId == keyId);
            state.EabCredentials[index] = state.EabCredentials[index] with { UsedAt = DateTimeOffset.UtcNow };
            await WriteUnsafeAsync(state, ct);
            return keyId;
        }
        finally { _gate.Release(); }
    }

    public async Task AssociateEabWithAccountAsync(string keyId, string accountId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAndMigrateUnsafeAsync(ct);
            var index = state.EabCredentials.FindIndex(credential => credential.KeyId == keyId);
            if (index < 0) return;
            state.EabCredentials[index] = state.EabCredentials[index] with { AccountId = accountId };
            await WriteUnsafeAsync(state, ct);
        }
        finally { _gate.Release(); }
    }

    private static string VerifyEab(JsonObject binding, JsonObject accountJwk, string expectedUrl, IReadOnlyList<AcmeEabCredentialState> credentials)
    {
        var protectedB64 = binding["protected"]?.GetValue<string>() ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding is missing protected.");
        var payloadB64 = binding["payload"]?.GetValue<string>() ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding is missing payload.");
        var signatureB64 = binding["signature"]?.GetValue<string>() ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding is missing signature.");
        var header = JsonNode.Parse(Rfc8555AcmeService.Base64UrlDecode(protectedB64))?.AsObject()
            ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding protected header is invalid.");
        var keyId = header["kid"]?.GetValue<string>();
        var credential = credentials.FirstOrDefault(value => value.KeyId == keyId);

        if (!string.Equals(header["alg"]?.GetValue<string>(), "HS256", StringComparison.Ordinal) ||
            credential is null || credential.RevokedAt is not null || credential.UsedAt is not null ||
            !string.Equals(header["url"]?.GetValue<string>(), expectedUrl, StringComparison.Ordinal))
            throw Rfc8555AcmeService.AcmeProblem("unauthorized", "externalAccountBinding does not match an active EAB credential.", 403);

        var payload = JsonNode.Parse(Rfc8555AcmeService.Base64UrlDecode(payloadB64))?.AsObject();
        if (payload is null || !JsonNode.DeepEquals(payload, accountJwk))
            throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding payload must contain the account JWK.");

        using var hmac = new HMACSHA256(Rfc8555AcmeService.Base64UrlDecode(credential.HmacKey));
        var expectedSignature = hmac.ComputeHash(Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}"));
        var signature = Rfc8555AcmeService.Base64UrlDecode(signatureB64);
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, signature))
            throw Rfc8555AcmeService.AcmeProblem("unauthorized", "externalAccountBinding signature is invalid.", 403);
        return credential.KeyId;
    }

    private async Task<AcmeAccessPolicyState> ReadAndMigrateUnsafeAsync(CancellationToken ct)
    {
        var state = await ReadUnsafeAsync(ct) ?? new AcmeAccessPolicyState([], []);
        state = state with { EabCredentials = state.EabCredentials ?? [] };
        if (state.EabCredentials.Count == 0 && !string.IsNullOrWhiteSpace(state.LegacyEabKeyId) && !string.IsNullOrWhiteSpace(state.LegacyEabHmacKey))
        {
            state = state with
            {
                EabCredentials = [new AcmeEabCredentialState(state.LegacyEabKeyId, "Legacy EAB credential", state.LegacyEabHmacKey, DateTimeOffset.UtcNow, null, null, null)],
                LegacyEabKeyId = null,
                LegacyEabHmacKey = null
            };
            await WriteUnsafeAsync(state, ct);
        }
        return state;
    }

    private static bool IsAllowlisted(IPAddress? address, IReadOnlyList<string> networks)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return networks.Any(network => TryParseNetwork(network, out var parsed) && parsed.Contains(address));
    }

    private static string[] NormalizeNetworks(IReadOnlyList<string>? networks) => (networks ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Select(value =>
    {
        if (!TryParseNetwork(value, out var network)) throw new ArgumentException("Every allowlisted ACME client network must be an IP address or CIDR, for example 192.168.10.25 or 192.168.10.0/24.");
        return network.ToString();
    }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool TryParseNetwork(string value, out IPNetwork network)
    {
        if (IPAddress.TryParse(value, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            network = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }
        return IPNetwork.TryParse(value, out network);
    }

    private async Task<AcmeAccessPolicyState?> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<AcmeAccessPolicyState>(stream, cancellationToken: ct);
    }

    private async Task WriteUnsafeAsync(AcmeAccessPolicyState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, state, cancellationToken: ct);
        File.Move(temp, _path, true);
    }

    private static AcmeAccessPolicy ToPublicPolicy(AcmeAccessPolicyState state) => new(state.AllowlistedClientNetworks, state.EabCredentials.Select(ToPublicCredential).OrderByDescending(credential => credential.CreatedAt).ToList(), true);
    private static AcmeEabCredential ToPublicCredential(AcmeEabCredentialState credential) => new(credential.KeyId, credential.Name, credential.CreatedAt, credential.UsedAt, credential.AccountId, credential.RevokedAt);
}

public sealed record UpdateAcmeAccessPolicyRequest(IReadOnlyList<string>? AllowlistedClientNetworks);
public sealed record CreateAcmeEabCredentialRequest(string? Name);
public sealed record AcmeAccessPolicy(IReadOnlyList<string> AllowlistedClientNetworks, IReadOnlyList<AcmeEabCredential> EabCredentials, bool EabRequiredForNonAllowlistedClients);
public sealed record AcmeEabCredential(string KeyId, string Name, DateTimeOffset CreatedAt, DateTimeOffset? UsedAt, string? AccountId, DateTimeOffset? RevokedAt);
public sealed record AcmeEabCredentials(string KeyId, string HmacKey);
internal sealed record AcmeAccessPolicyState(
    string[] AllowlistedClientNetworks,
    List<AcmeEabCredentialState> EabCredentials,
    [property: JsonPropertyName("EabKeyId")] string? LegacyEabKeyId = null,
    [property: JsonPropertyName("EabHmacKey")] string? LegacyEabHmacKey = null);
internal sealed record AcmeEabCredentialState(string KeyId, string Name, string HmacKey, DateTimeOffset CreatedAt, DateTimeOffset? UsedAt, string? AccountId, DateTimeOffset? RevokedAt);
