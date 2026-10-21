using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Stores deployment targets: devices that fetch the current certificate of a renewal chain with their own
/// token. Only a SHA-256 hash of each token is kept; the token itself is shown once when it is created.
/// </summary>
public sealed class DeploymentTargetRegistry(HomeCaStorage storage, ILogger<DeploymentTargetRegistry> logger)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "deployment-targets.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<DeploymentTarget>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await ReadUnsafeAsync(ct)).Select(target => target.ToPublic()).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<DeploymentTarget?> GetAsync(string id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(target => target.Id == id);

    public async Task<CreatedDeploymentTarget> CreateAsync(SaveDeploymentTargetRequest request, CancellationToken ct)
    {
        var normalized = Validate(request);
        var token = CreateToken();
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            if (items.Any(target => target.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A deployment target with this name already exists.");
            var stored = new StoredDeploymentTarget(Guid.NewGuid().ToString("N"), normalized.Name, normalized.CertificateId, HashToken(token),
                normalized.Host, normalized.Port, normalized.WebhookUrl, DateTimeOffset.UtcNow);
            items.Add(stored);
            await WriteAtomicAsync(items, ct);
            logger.LogInformation("Created deployment target {TargetId} for certificate {CertificateId}", stored.Id, stored.CertificateId);
            return new CreatedDeploymentTarget(stored.ToPublic(), token);
        }
        finally { _gate.Release(); }
    }

    public async Task<DeploymentTarget?> UpdateAsync(string id, SaveDeploymentTargetRequest request, CancellationToken ct)
    {
        var normalized = Validate(request);
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            var index = items.FindIndex(target => target.Id == id);
            if (index < 0) return null;
            if (items.Any(target => target.Id != id && target.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A deployment target with this name already exists.");
            var certificateChanged = !string.Equals(items[index].CertificateId, normalized.CertificateId, StringComparison.OrdinalIgnoreCase);
            items[index] = items[index] with
            {
                Name = normalized.Name,
                CertificateId = normalized.CertificateId,
                Host = normalized.Host,
                Port = normalized.Port,
                WebhookUrl = normalized.WebhookUrl,
                LastProbe = certificateChanged ? null : items[index].LastProbe
            };
            await WriteAtomicAsync(items, ct);
            return items[index].ToPublic();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            if (items.RemoveAll(target => target.Id == id) == 0) return false;
            await WriteAtomicAsync(items, ct);
            logger.LogInformation("Deleted deployment target {TargetId}", id);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Replaces the token, for example after a device was reinstalled or the token leaked.</summary>
    public async Task<string?> RotateTokenAsync(string id, CancellationToken ct)
    {
        var token = CreateToken();
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            var index = items.FindIndex(target => target.Id == id);
            if (index < 0) return null;
            items[index] = items[index] with { TokenHash = HashToken(token) };
            await WriteAtomicAsync(items, ct);
            logger.LogInformation("Rotated the token of deployment target {TargetId}", id);
            return token;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns the target when the presented token matches, compared in constant time.</summary>
    public async Task<DeploymentTarget?> AuthenticateAsync(string id, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await _gate.WaitAsync(ct);
        try
        {
            var target = (await ReadUnsafeAsync(ct)).FirstOrDefault(item => item.Id == id);
            if (target is null) return null;
            var expected = Encoding.ASCII.GetBytes(target.TokenHash);
            var actual = Encoding.ASCII.GetBytes(HashToken(token.Trim()));
            return CryptographicOperations.FixedTimeEquals(expected, actual) ? target.ToPublic() : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Moves every target of a renewed certificate to its successor and returns the moved targets.</summary>
    public async Task<IReadOnlyList<DeploymentTarget>> ReplaceCertificateAsync(string previousCertificateId, string renewedCertificateId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            var moved = new List<DeploymentTarget>();
            for (var index = 0; index < items.Count; index++)
            {
                if (!string.Equals(items[index].CertificateId, previousCertificateId, StringComparison.OrdinalIgnoreCase)) continue;
                items[index] = items[index] with { CertificateId = renewedCertificateId, LastProbe = null };
                moved.Add(items[index].ToPublic());
            }
            if (moved.Count > 0) await WriteAtomicAsync(items, ct);
            return moved;
        }
        finally { _gate.Release(); }
    }

    public Task RecordCheckInAsync(string id, CancellationToken ct) =>
        UpdateStoredAsync(id, target => target with { LastCheckInAt = DateTimeOffset.UtcNow }, ct);

    public Task RecordDownloadAsync(string id, string fingerprint, CancellationToken ct) =>
        UpdateStoredAsync(id, target => target with { LastDownloadedFingerprint = fingerprint, LastDownloadedAt = DateTimeOffset.UtcNow }, ct);

    public Task RecordProbeAsync(string id, DeploymentProbeResult result, CancellationToken ct) =>
        UpdateStoredAsync(id, target => target with { LastProbe = result }, ct);

    private async Task UpdateStoredAsync(string id, Func<StoredDeploymentTarget, StoredDeploymentTarget> update, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            var index = items.FindIndex(target => target.Id == id);
            if (index < 0) return;
            items[index] = update(items[index]);
            await WriteAtomicAsync(items, ct);
        }
        finally { _gate.Release(); }
    }

    internal static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static SaveDeploymentTargetRequest Validate(SaveDeploymentTargetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80) throw new ArgumentException("A name of up to 80 characters is required.");
        if (string.IsNullOrWhiteSpace(request.CertificateId) || !request.CertificateId.Trim().All(Uri.IsHexDigit)) throw new ArgumentException("A certificate is required.");
        var host = string.IsNullOrWhiteSpace(request.Host) ? null : request.Host.Trim();
        if (host is not null && Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ArgumentException("The host must be a DNS name or an IP address.");
        if (request.Port is < 1 or > 65535) throw new ArgumentException("The port must be between 1 and 65535.");
        var webhook = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl.Trim();
        if (webhook is not null && (!Uri.TryCreate(webhook, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("The webhook must be an absolute http or https URL.");
        return new SaveDeploymentTargetRequest(request.Name.Trim(), request.CertificateId.Trim().ToLowerInvariant(), host, request.Port, webhook);
    }

    private async Task<List<StoredDeploymentTarget>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<StoredDeploymentTarget>>(stream, cancellationToken: ct) ?? [];
    }

    private async Task WriteAtomicAsync(List<StoredDeploymentTarget> items, CancellationToken ct)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, items, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }

    private sealed record StoredDeploymentTarget(
        string Id, string Name, string CertificateId, string TokenHash, string? Host, int Port, string? WebhookUrl, DateTimeOffset CreatedAt,
        DateTimeOffset? LastCheckInAt = null, string? LastDownloadedFingerprint = null, DateTimeOffset? LastDownloadedAt = null, DeploymentProbeResult? LastProbe = null)
    {
        public DeploymentTarget ToPublic() => new(Id, Name, CertificateId, Host, Port, WebhookUrl, CreatedAt, LastCheckInAt, LastDownloadedFingerprint, LastDownloadedAt, LastProbe);
    }
}

public sealed record SaveDeploymentTargetRequest(string Name, string CertificateId, string? Host = null, int Port = 443, string? WebhookUrl = null);
public sealed record DeploymentTarget(
    string Id, string Name, string CertificateId, string? Host, int Port, string? WebhookUrl, DateTimeOffset CreatedAt,
    DateTimeOffset? LastCheckInAt, string? LastDownloadedFingerprint, DateTimeOffset? LastDownloadedAt, DeploymentProbeResult? LastProbe);
public sealed record CreatedDeploymentTarget(DeploymentTarget Target, string Token);

/// <summary>Result of connecting to a target and comparing the served certificate with the current one.</summary>
public sealed record DeploymentProbeResult(DateTimeOffset CheckedAt, string Status, string? ServedFingerprint = null, DateTime? ServedNotAfter = null, string? Error = null)
{
    public const string Current = "current";
    public const string Outdated = "outdated";
    public const string Foreign = "foreign";
    public const string Unreachable = "unreachable";
}
