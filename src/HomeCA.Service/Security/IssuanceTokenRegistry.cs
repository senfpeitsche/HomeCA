using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Security;

/// <summary>
/// Stores scoped issuance tokens: revocable API keys that may only issue, sign and export certificates for an
/// administrator-defined set of names. Only a SHA-256 hash of each secret is kept; the secret is returned once.
/// </summary>
public sealed class IssuanceTokenRegistry(HomeCaStorage storage, ILogger<IssuanceTokenRegistry> logger)
{
    public const string SecretPrefix = "hca_it_";
    public const int MaximumValidityDays = 730;
    public const int DefaultMaxValidityDays = 30;
    internal static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(1);
    internal static readonly string[] Usages = ["TLS", "mTLS"];

    private readonly string _path = Path.Combine(storage.RootPath, "state", "issuance-tokens.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<IssuanceToken>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await ReadUnsafeAsync(ct)).Select(token => token.ToPublic()).OrderBy(token => token.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<IssuanceToken?> GetAsync(string id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(token => token.Id == id);

    public async Task<CreatedIssuanceToken> CreateAsync(CreateIssuanceTokenRequest request, string? createdBy, CancellationToken ct)
    {
        var normalized = Validate(request);
        var secret = SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            if (items.Any(token => token.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An issuance token with this name already exists.");
            var stored = new StoredIssuanceToken(Guid.NewGuid().ToString("N"), normalized.Name, HashSecret(secret), normalized.AllowedDnsSuffixes,
                normalized.AllowedIpCidrs ?? [], normalized.AllowedUsages ?? ["TLS"], normalized.MaxValidityDays, normalized.ExpiresAt,
                DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(createdBy) ? "administrator" : createdBy);
            items.Add(stored);
            await WriteAtomicAsync(items, ct);
            logger.LogInformation("Created issuance token {TokenId} ({TokenName})", stored.Id, stored.Name);
            return new CreatedIssuanceToken(stored.ToPublic(), secret);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Marks the token as revoked; the record stays for the audit trail and the certificate owner link.</summary>
    public async Task<IssuanceToken?> RevokeAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            var index = items.FindIndex(token => token.Id == id);
            if (index < 0) return null;
            if (items[index].RevokedAt is null)
            {
                items[index] = items[index] with { RevokedAt = DateTimeOffset.UtcNow };
                await WriteAtomicAsync(items, ct);
                logger.LogInformation("Revoked issuance token {TokenId}", id);
            }
            return items[index].ToPublic();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns the token whose hash matches the secret, compared in constant time, whatever its status.</summary>
    public async Task<IssuanceToken?> ResolveAsync(string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal)) return null;
        var actual = Encoding.ASCII.GetBytes(HashSecret(secret.Trim()));
        await _gate.WaitAsync(ct);
        try
        {
            IssuanceToken? match = null;
            // Every record is compared so the time taken does not reveal the position of a match.
            foreach (var token in await ReadUnsafeAsync(ct))
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token.TokenHash), actual)) match = token.ToPublic();
            return match;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records a use; written at most once per <see cref="LastUsedInterval"/> to limit disk writes.</summary>
    public async Task TouchAsync(string id, DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            var index = items.FindIndex(token => token.Id == id);
            if (index < 0 || items[index].LastUsedAt is { } last && now - last < LastUsedInterval) return;
            items[index] = items[index] with { LastUsedAt = now };
            await WriteAtomicAsync(items, ct);
        }
        finally { _gate.Release(); }
    }

    internal static string HashSecret(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static CreateIssuanceTokenRequest Validate(CreateIssuanceTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80) throw new ArgumentException("A name of up to 80 characters is required.");
        var suffixes = new List<string>();
        foreach (var entry in request.AllowedDnsSuffixes ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var suffix = IssuanceTokenScope.NormalizeDnsName(entry.Trim().TrimStart('*').TrimStart('.'));
            if (suffix is null || !suffix.Contains('.')) throw new ArgumentException($"'{entry}' is not a DNS suffix such as test.home.lan.");
            if (!suffixes.Contains(suffix)) suffixes.Add(suffix);
        }
        var cidrs = new List<string>();
        foreach (var entry in request.AllowedIpCidrs ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (!IPNetwork.TryParse(entry.Trim(), out var network)) throw new ArgumentException($"'{entry}' is not a CIDR network such as 10.0.0.0/24.");
            if (!cidrs.Contains(network.ToString())) cidrs.Add(network.ToString());
        }
        if (suffixes.Count == 0 && cidrs.Count == 0) throw new ArgumentException("At least one DNS suffix or IP network is required.");
        var usages = (request.AllowedUsages ?? []).Where(usage => !string.IsNullOrWhiteSpace(usage))
            .Select(usage => Usages.FirstOrDefault(known => known.Equals(usage.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"Unknown usage '{usage}'."))
            .Distinct().ToList();
        if (usages.Count == 0) usages.Add("TLS");
        if (request.MaxValidityDays is < 1 or > MaximumValidityDays) throw new ArgumentException($"The maximum validity must be between 1 and {MaximumValidityDays} days.");
        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("The token expiry must lie in the future.");
        return new CreateIssuanceTokenRequest(request.Name.Trim(), suffixes, cidrs, usages, request.MaxValidityDays, request.ExpiresAt);
    }

    private async Task<List<StoredIssuanceToken>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<StoredIssuanceToken>>(stream, cancellationToken: ct) ?? [];
    }

    private async Task WriteAtomicAsync(List<StoredIssuanceToken> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, items, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }

    private sealed record StoredIssuanceToken(
        string Id, string Name, string TokenHash, IReadOnlyList<string> AllowedDnsSuffixes, IReadOnlyList<string> AllowedIpCidrs,
        IReadOnlyList<string> AllowedUsages, int MaxValidityDays, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, string CreatedBy,
        DateTimeOffset? LastUsedAt = null, DateTimeOffset? RevokedAt = null)
    {
        public IssuanceToken ToPublic() => new(Id, Name, AllowedDnsSuffixes, AllowedIpCidrs, AllowedUsages, MaxValidityDays, ExpiresAt, CreatedAt, CreatedBy, LastUsedAt, RevokedAt);
    }
}

public sealed record CreateIssuanceTokenRequest(
    string Name, IReadOnlyList<string> AllowedDnsSuffixes, IReadOnlyList<string>? AllowedIpCidrs = null, IReadOnlyList<string>? AllowedUsages = null,
    int MaxValidityDays = IssuanceTokenRegistry.DefaultMaxValidityDays, DateTimeOffset? ExpiresAt = null);

/// <summary>An issuance token as the API and the UI see it: scope and status, never the secret or its hash.</summary>
public sealed record IssuanceToken(
    string Id, string Name, IReadOnlyList<string> AllowedDnsSuffixes, IReadOnlyList<string> AllowedIpCidrs, IReadOnlyList<string> AllowedUsages,
    int MaxValidityDays, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt)
{
    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

public sealed record CreatedIssuanceToken(IssuanceToken Token, string Secret);
