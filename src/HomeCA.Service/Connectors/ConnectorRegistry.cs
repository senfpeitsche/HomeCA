using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Connectors;

/// <summary>Persists provider-neutral connector instances and their isolated secret dictionaries.</summary>
public sealed class ConnectorRegistry(HomeCaStorage storage, ConnectorCatalog catalog)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "connectors.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<ConnectorRegistration>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadUnsafeAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<ConnectorRegistration?> GetAsync(string id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(connector => connector.Id == id);

    public async Task<ConnectorRegistration> AddAsync(CreateConnectorRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !catalog.Types.Contains(request.Type, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("A name and a supported connector type are required.");
        await _gate.WaitAsync(ct);
        try
        {
            var connectors = (await ReadUnsafeAsync(ct)).ToList();
            if (connectors.Any(connector => connector.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A connector with this name already exists.");
            var connector = new ConnectorRegistration(Guid.NewGuid().ToString("N"), request.Name.Trim(), request.Type.Trim().ToLowerInvariant(), new Dictionary<string, string>(request.Secrets, StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow);
            connectors.Add(connector);
            await WriteAtomicAsync(connectors, ct);
            return connector;
        }
        finally { _gate.Release(); }
    }

    public async Task<ConnectorRegistration?> UpdateAsync(string id, CreateConnectorRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !catalog.Types.Contains(request.Type, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("A name and a supported connector type are required.");
        await _gate.WaitAsync(ct);
        try
        {
            var connectors = (await ReadUnsafeAsync(ct)).ToList();
            var index = connectors.FindIndex(connector => connector.Id == id);
            if (index < 0) return null;
            if (connectors.Any(connector => connector.Id != id && connector.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A connector with this name already exists.");
            var existing = connectors[index];
            var updated = new ConnectorRegistration(existing.Id, request.Name.Trim(), request.Type.Trim().ToLowerInvariant(), new Dictionary<string, string>(request.Secrets, StringComparer.OrdinalIgnoreCase), existing.CreatedAt);
            connectors[index] = updated;
            await WriteAtomicAsync(connectors, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var connectors = (await ReadUnsafeAsync(ct)).ToList();
            var removed = connectors.RemoveAll(connector => connector.Id == id);
            if (removed == 0) return false;
            await WriteAtomicAsync(connectors, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<ConnectorRegistration>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<ConnectorRegistration>>(stream, cancellationToken: ct) ?? [];
    }

    private async Task WriteAtomicAsync<T>(T value, CancellationToken ct)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }
}

public sealed record CreateConnectorRequest(string Name, string Type, IReadOnlyDictionary<string, string> Secrets);
public sealed record ConnectorRegistration(string Id, string Name, string Type, IReadOnlyDictionary<string, string> Secrets, DateTimeOffset CreatedAt);
