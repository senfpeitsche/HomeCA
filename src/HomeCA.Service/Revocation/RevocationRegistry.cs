using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Revocation;

public sealed class RevocationRegistry(HomeCaStorage storage)
{
    private readonly string _path = Path.Combine(storage.RootPath, "crl", "revocations.json");
    public async Task<IReadOnlyList<RevocationRecord>> ListAsync(CancellationToken cancellationToken) => File.Exists(_path) ? await JsonSerializer.DeserializeAsync<List<RevocationRecord>>(File.OpenRead(_path), cancellationToken: cancellationToken) ?? [] : [];
    public async Task<RevocationRecord> RevokeAsync(string serialNumber, string reason, CancellationToken cancellationToken)
    {
        var records = (await ListAsync(cancellationToken)).ToList();
        var record = new RevocationRecord(serialNumber, reason, DateTimeOffset.UtcNow);
        records.RemoveAll(item => item.SerialNumber.Equals(serialNumber, StringComparison.OrdinalIgnoreCase)); records.Add(record);
        await using var stream = File.Create(_path); await JsonSerializer.SerializeAsync(stream, records, cancellationToken: cancellationToken);
        return record;
    }
}
public sealed record RevocationRecord(string SerialNumber, string Reason, DateTimeOffset RevokedAt);
