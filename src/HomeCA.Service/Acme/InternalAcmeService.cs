using System.Text.Json;
using HomeCA.Service.Domains;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Acme;

public sealed class InternalAcmeService(HomeCaStorage storage, DomainRegistry domains)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "acme-orders.json");
    public async Task<AcmeOrder> CreateOrderAsync(string accountId, IReadOnlyList<string> identifiers, CancellationToken cancellationToken)
    {
        var zones = (await domains.ListAsync(cancellationToken)).Where(domain => domain.InternalIssuanceEnabled).Select(domain => domain.Name).ToList();
        if (identifiers.Count == 0 || identifiers.Any(name => !zones.Any(zone => name.Equals(zone, StringComparison.OrdinalIgnoreCase) || name.EndsWith('.' + zone, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException("All identifiers must be under an active internal issuance zone.");
        var orders = File.Exists(_path) ? await JsonSerializer.DeserializeAsync<List<AcmeOrder>>(File.OpenRead(_path), cancellationToken: cancellationToken) ?? [] : [];
        var order = new AcmeOrder(Guid.NewGuid().ToString("N"), accountId, identifiers, "pending", DateTimeOffset.UtcNow);
        orders.Add(order); await using var stream = File.Create(_path); await JsonSerializer.SerializeAsync(stream, orders, cancellationToken: cancellationToken); return order;
    }
}
public sealed record AcmeOrder(string Id, string AccountId, IReadOnlyList<string> Identifiers, string Status, DateTimeOffset CreatedAt);

public sealed record AcmeOrderRequest(string AccountId, IReadOnlyList<string> Identifiers);
