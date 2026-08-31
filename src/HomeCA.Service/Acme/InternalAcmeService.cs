using System.Text.Json;
using HomeCA.Service.Domains;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;

namespace HomeCA.Service.Acme;

/// <summary>Provides the stateful operations behind HomeCA's internal ACME directory.</summary>
public sealed class InternalAcmeService(HomeCaStorage storage, DomainRegistry domains, CertificateIssuanceService certificates, ILogger<InternalAcmeService> logger)
{
    private readonly string _accountsPath = Path.Combine(storage.RootPath, "state", "acme-accounts.json");
    private readonly string _ordersPath = Path.Combine(storage.RootPath, "state", "acme-orders.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<InternalAcmeDirectory> GetDirectoryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new InternalAcmeDirectory("/api/v1/acme/accounts", "/api/v1/acme/orders", "/api/v1/acme/orders/{orderId}/finalize"));

    public async Task<AcmeAccount> RegisterAccountAsync(RegisterAcmeAccountRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Contact)) throw new ArgumentException("An ACME account contact is required.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var accounts = await ReadAsync<List<AcmeAccount>>(_accountsPath, cancellationToken) ?? [];
            var existing = accounts.FirstOrDefault(account => account.Contact.Equals(request.Contact.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            var account = new AcmeAccount(Guid.NewGuid().ToString("N"), request.Contact.Trim(), DateTimeOffset.UtcNow);
            accounts.Add(account);
            await WriteAsync(_accountsPath, accounts, cancellationToken);
            logger.LogInformation("Registered ACME account {AccountId} ({Contact})", account.Id, account.Contact);
            return account;
        }
        finally { _gate.Release(); }
    }

    public async Task<AcmeOrder> CreateOrderAsync(string accountId, IReadOnlyList<string> identifiers, CancellationToken cancellationToken)
    {
        var normalizedIdentifiers = NormalizeIdentifiers(identifiers);
        var zones = (await domains.ListAsync(cancellationToken)).Where(domain => domain.InternalIssuanceEnabled).Select(domain => domain.Name).ToList();
        if (zones.Count == 0 || normalizedIdentifiers.Any(name => !zones.Any(zone => IsWithinZone(name, zone)))) throw new InvalidOperationException("All identifiers must be under an active internal issuance zone.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var accounts = await ReadAsync<List<AcmeAccount>>(_accountsPath, cancellationToken) ?? [];
            if (!accounts.Any(account => account.Id == accountId)) throw new InvalidOperationException("ACME account is not registered.");
            var orders = await ReadAsync<List<AcmeOrder>>(_ordersPath, cancellationToken) ?? [];
            var order = new AcmeOrder(Guid.NewGuid().ToString("N"), accountId, normalizedIdentifiers, "ready", DateTimeOffset.UtcNow, null);
            orders.Add(order);
            await WriteAsync(_ordersPath, orders, cancellationToken);
            logger.LogInformation("Created ACME order {OrderId} for identifiers {Identifiers}", order.Id, string.Join(", ", normalizedIdentifiers));
            return order;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AcmeAccount>> ListAccountsAsync(CancellationToken cancellationToken) => await ReadAsync<List<AcmeAccount>>(_accountsPath, cancellationToken) ?? [];

    public async Task<IReadOnlyList<AcmeOrder>> ListOrdersAsync(CancellationToken cancellationToken) => await ReadAsync<List<AcmeOrder>>(_ordersPath, cancellationToken) ?? [];

    public async Task<AcmeOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken) => (await ReadAsync<List<AcmeOrder>>(_ordersPath, cancellationToken) ?? []).FirstOrDefault(order => order.Id == orderId);

    public async Task<AcmeOrder> FinalizeOrderAsync(string orderId, FinalizeAcmeOrderRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var orders = await ReadAsync<List<AcmeOrder>>(_ordersPath, cancellationToken) ?? [];
            var index = orders.FindIndex(order => order.Id == orderId);
            if (index < 0) throw new KeyNotFoundException("ACME order was not found.");
            var order = orders[index];
            if (order.Status == "valid") return order;
            if (order.Status != "ready") throw new InvalidOperationException("ACME order is not ready for finalization.");
            var result = await certificates.IssueAsync(new IssueCertificateRequest("TLS", order.Identifiers, [], request.ValidityDays, request.KeyAlgorithm, request.RsaKeySize), cancellationToken);
            order = order with { Status = "valid", CertificateId = result.Id };
            orders[index] = order;
            await WriteAsync(_ordersPath, orders, cancellationToken);
            logger.LogInformation("Finalized ACME order {OrderId}, issued certificate {CertificateId}", orderId, result.Id);
            return order;
        }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<string> NormalizeIdentifiers(IReadOnlyList<string> identifiers)
    {
        var normalized = identifiers.Where(identifier => !string.IsNullOrWhiteSpace(identifier)).Select(identifier => identifier.Trim().TrimEnd('.').ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (normalized.Count == 0 || normalized.Any(identifier => identifier.Contains('*') || Uri.CheckHostName(identifier) == UriHostNameType.Unknown)) throw new ArgumentException("At least one valid, non-wildcard DNS identifier is required.");
        return normalized;
    }

    private static bool IsWithinZone(string name, string zone) => name.Equals(zone, StringComparison.OrdinalIgnoreCase) || name.EndsWith('.' + zone, StringComparison.OrdinalIgnoreCase);
    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(temporaryPath, path, true);
    }
}

public sealed record InternalAcmeDirectory(string NewAccount, string NewOrder, string FinalizeOrder);
public sealed record RegisterAcmeAccountRequest(string Contact);
public sealed record AcmeAccount(string Id, string Contact, DateTimeOffset CreatedAt);
public sealed record AcmeOrderRequest(string AccountId, IReadOnlyList<string> Identifiers);
public sealed record FinalizeAcmeOrderRequest(int ValidityDays = 365, string KeyAlgorithm = "ECC", int RsaKeySize = 2048);
public sealed record AcmeOrder(string Id, string AccountId, IReadOnlyList<string> Identifiers, string Status, DateTimeOffset CreatedAt, string? CertificateId);
