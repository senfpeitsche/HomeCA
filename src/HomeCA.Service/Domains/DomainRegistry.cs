using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Domains;

public sealed class DomainRegistry(HomeCaStorage storage)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "domains.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<IReadOnlyList<DomainRegistration>> ListAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<DomainRegistration>>(stream, cancellationToken: cancellationToken) ?? [];
    }

    public async Task<DomainRegistration> AddAsync(CreateDomainRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
        var domains = (await ListAsync(cancellationToken)).ToList();
        if (domains.Any(domain => domain.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Domain already exists.");
        var domain = new DomainRegistration(request.Name.Trim().TrimEnd('.').ToLowerInvariant(), request.InternalIssuanceEnabled, request.ConnectorId, DateTimeOffset.UtcNow);
        domains.Add(domain);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, domains, cancellationToken: cancellationToken);
        return domain;
        }
        finally { _gate.Release(); }
    }

    public async Task<DomainRegistration?> UpdateAsync(string name, CreateDomainRequest request, CancellationToken cancellationToken)
    {
        var normalizedName = request.Name.Trim().TrimEnd('.').ToLowerInvariant();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var domains = (await ListAsync(cancellationToken)).ToList();
            var index = domains.FindIndex(domain => domain.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            if (domains.Any(domain => !domain.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && domain.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Domain already exists.");
            var existing = domains[index];
            var updated = new DomainRegistration(normalizedName, request.InternalIssuanceEnabled, request.ConnectorId, existing.CreatedAt);
            domains[index] = updated;
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, domains, cancellationToken: cancellationToken);
            return updated;
        }
        finally { _gate.Release(); }
    }
}

public sealed record CreateDomainRequest(string Name, bool InternalIssuanceEnabled, string? ConnectorId);
public sealed record DomainRegistration(string Name, bool InternalIssuanceEnabled, string? ConnectorId, DateTimeOffset CreatedAt);
