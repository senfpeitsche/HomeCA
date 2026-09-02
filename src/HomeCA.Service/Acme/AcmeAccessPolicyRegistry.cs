using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Acme;

/// <summary>
/// Stores the access policy for the RFC 8555 endpoint. Clients connecting from an
/// allowlisted address or CIDR may register accounts without EAB. Every other
/// client must prove possession of the configured EAB HMAC key.
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
            var state = await ReadUnsafeAsync(ct) ?? CreateState();
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
            var state = await ReadUnsafeAsync(ct) ?? CreateState();
            state = state with { AllowlistedClientNetworks = networks };
            await WriteUnsafeAsync(state, ct);
            return ToPublicPolicy(state);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Generates a replacement EAB credential. The secret is returned only by this operation.</summary>
    public async Task<AcmeEabCredentials> RotateEabAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadUnsafeAsync(ct) ?? CreateState();
            state = state with
            {
                EabKeyId = Guid.NewGuid().ToString("N"),
                EabHmacKey = Rfc8555AcmeService.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))
            };
            await WriteUnsafeAsync(state, ct);
            return new AcmeEabCredentials(state.EabKeyId, state.EabHmacKey);
        }
        finally { _gate.Release(); }
    }

    public async Task ValidateNewAccountAsync(IPAddress? remoteAddress, JsonObject? externalAccountBinding, JsonObject accountJwk, string expectedUrl, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadUnsafeAsync(ct) ?? CreateState();
            if (!File.Exists(_path)) await WriteUnsafeAsync(state, ct);

            if (IsAllowlisted(remoteAddress, state.AllowlistedClientNetworks)) return;
            if (externalAccountBinding is null)
                throw Rfc8555AcmeService.AcmeProblem("externalAccountRequired", "This ACME client is not allowlisted and must provide externalAccountBinding.", 400);

            VerifyEab(externalAccountBinding, accountJwk, expectedUrl, state);
        }
        finally { _gate.Release(); }
    }

    private static void VerifyEab(JsonObject binding, JsonObject accountJwk, string expectedUrl, AcmeAccessPolicyState state)
    {
        var protectedB64 = binding["protected"]?.GetValue<string>() ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding is missing protected.");
        var payloadB64 = binding["payload"]?.GetValue<string>() ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding is missing payload.");
        var signatureB64 = binding["signature"]?.GetValue<string>() ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding is missing signature.");
        var header = JsonNode.Parse(Rfc8555AcmeService.Base64UrlDecode(protectedB64))?.AsObject()
            ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding protected header is invalid.");

        if (!string.Equals(header["alg"]?.GetValue<string>(), "HS256", StringComparison.Ordinal) ||
            !string.Equals(header["kid"]?.GetValue<string>(), state.EabKeyId, StringComparison.Ordinal) ||
            !string.Equals(header["url"]?.GetValue<string>(), expectedUrl, StringComparison.Ordinal))
            throw Rfc8555AcmeService.AcmeProblem("unauthorized", "externalAccountBinding does not match this ACME server.", 403);

        var payload = JsonNode.Parse(Rfc8555AcmeService.Base64UrlDecode(payloadB64))?.AsObject();
        if (payload is null || !JsonNode.DeepEquals(payload, accountJwk))
            throw Rfc8555AcmeService.AcmeProblem("malformed", "externalAccountBinding payload must contain the account JWK.");

        var key = Rfc8555AcmeService.Base64UrlDecode(state.EabHmacKey);
        using var hmac = new HMACSHA256(key);
        var expectedSignature = hmac.ComputeHash(Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}"));
        var signature = Rfc8555AcmeService.Base64UrlDecode(signatureB64);
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, signature))
            throw Rfc8555AcmeService.AcmeProblem("unauthorized", "externalAccountBinding signature is invalid.", 403);
    }

    private static bool IsAllowlisted(IPAddress? address, IReadOnlyList<string> networks)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return networks.Any(network => TryParseNetwork(network, out var parsed) && parsed.Contains(address));
    }

    private static string[] NormalizeNetworks(IReadOnlyList<string>? networks)
    {
        var normalized = (networks ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Select(value =>
        {
            if (!TryParseNetwork(value, out var network))
                throw new ArgumentException("Every allowlisted ACME client network must be an IP address or CIDR, for example 192.168.10.25 or 192.168.10.0/24.");
            return network.ToString();
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return normalized;
    }

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
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, state, cancellationToken: ct);
        File.Move(temp, _path, true);
    }

    private static AcmeAccessPolicyState CreateState() => new([], Guid.NewGuid().ToString("N"), Rfc8555AcmeService.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
    private static AcmeAccessPolicy ToPublicPolicy(AcmeAccessPolicyState state) => new(state.AllowlistedClientNetworks, state.EabKeyId, true);
}

public sealed record UpdateAcmeAccessPolicyRequest(IReadOnlyList<string>? AllowlistedClientNetworks);
public sealed record AcmeAccessPolicy(IReadOnlyList<string> AllowlistedClientNetworks, string EabKeyId, bool EabRequiredForNonAllowlistedClients);
public sealed record AcmeEabCredentials(string KeyId, string HmacKey);
internal sealed record AcmeAccessPolicyState(string[] AllowlistedClientNetworks, string EabKeyId, string EabHmacKey);
