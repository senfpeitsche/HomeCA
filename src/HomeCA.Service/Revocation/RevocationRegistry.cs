using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Revocation;

public sealed class RevocationRegistry(HomeCaStorage storage, ILogger<RevocationRegistry> logger)
{
    private readonly string _path = Path.Combine(storage.RootPath, "crl", "revocations.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<RevocationRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnsafeAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<RevocationRecord> RevokeAsync(string serialNumber, string reason, CancellationToken cancellationToken, string? authorityId = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = (await ReadUnsafeAsync(cancellationToken)).ToList();
            var record = new RevocationRecord(serialNumber, reason, DateTimeOffset.UtcNow, authorityId);
            records.RemoveAll(item => item.SerialNumber.Equals(serialNumber, StringComparison.OrdinalIgnoreCase));
            records.Add(record);
            await WriteAtomicAsync(records, cancellationToken);
            logger.LogInformation("Revoked certificate {SerialNumber}, reason: {Reason}", serialNumber, reason);
            return record;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<RevocationRecord>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<RevocationRecord>>(stream, cancellationToken: ct) ?? [];
    }

    private async Task WriteAtomicAsync<T>(T value, CancellationToken ct)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }
}

public sealed record RevocationRecord(string SerialNumber, string Reason, DateTimeOffset RevokedAt, string? AuthorityId = null);
