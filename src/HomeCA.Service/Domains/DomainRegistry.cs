using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Domains;

public sealed class DomainRegistry(HomeCaStorage storage)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "domains.json");
    public async Task<IReadOnlyList<DomainRegistration>> ListAsync(CancellationToken cancellationToken) =>
        File.Exists(_path) ? await JsonSerializer.DeserializeAsync<List<DomainRegistration>>(File.OpenRead(_path), cancellationToken: cancellationToken) ?? [] : [];

    public async Task<DomainRegistration> AddAsync(CreateDomainRequest request, CancellationToken cancellationToken)
    {
        var domains = (await ListAsync(cancellationToken)).ToList();
        if (domains.Any(domain => domain.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Domain already exists.");
        var domain = new DomainRegistration(request.Name.Trim().TrimEnd('.').ToLowerInvariant(), request.InternalIssuanceEnabled, request.ConnectorType, DateTimeOffset.UtcNow);
        domains.Add(domain);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, domains, cancellationToken: cancellationToken);
        return domain;
    }
}

public sealed record CreateDomainRequest(string Name, bool InternalIssuanceEnabled, string? ConnectorType);
public sealed record DomainRegistration(string Name, bool InternalIssuanceEnabled, string? ConnectorType, DateTimeOffset CreatedAt);
