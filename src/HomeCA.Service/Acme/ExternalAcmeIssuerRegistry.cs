using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Acme;

/// <summary>Stores external ACME directories as managed issuer configuration; DNS-01 execution is delegated to a DNS connector.</summary>
public sealed class ExternalAcmeIssuerRegistry(HomeCaStorage storage, ILogger<ExternalAcmeIssuerRegistry> logger)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "external-acme-issuers.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<ExternalAcmeIssuer>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadUnsafeAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<ExternalAcmeIssuer> AddAsync(CreateExternalAcmeIssuerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ConnectorId)) throw new ArgumentException("Issuer name and DNS connector instance are required.");
        if (!Uri.TryCreate(request.DirectoryUrl, UriKind.Absolute, out var directory) || directory.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A valid HTTPS directory URL is required.");
        await _gate.WaitAsync(ct);
        try
        {
            var issuers = (await ReadUnsafeAsync(ct)).ToList();
            if (issuers.Any(issuer => issuer.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("An issuer with this name already exists.");
            var issuer = new ExternalAcmeIssuer(Guid.NewGuid().ToString("N"), request.Name.Trim(), directory.AbsoluteUri, request.ConnectorId.Trim(), DateTimeOffset.UtcNow);
            issuers.Add(issuer);
            await WriteAtomicAsync(issuers, ct);
            logger.LogInformation("Added external ACME issuer {IssuerId} ({Name})", issuer.Id, issuer.Name);
            return issuer;
        }
        finally { _gate.Release(); }
    }

    public async Task<ExternalAcmeIssuer?> UpdateAsync(string id, CreateExternalAcmeIssuerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ConnectorId)) throw new ArgumentException("Issuer name and DNS connector instance are required.");
        if (!Uri.TryCreate(request.DirectoryUrl, UriKind.Absolute, out var directory) || directory.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A valid HTTPS directory URL is required.");
        await _gate.WaitAsync(ct);
        try
        {
            var issuers = (await ReadUnsafeAsync(ct)).ToList();
            var index = issuers.FindIndex(issuer => issuer.Id == id);
            if (index < 0) return null;
            if (issuers.Any(issuer => issuer.Id != id && issuer.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("An issuer with this name already exists.");
            var updated = new ExternalAcmeIssuer(issuers[index].Id, request.Name.Trim(), directory.AbsoluteUri, request.ConnectorId.Trim(), issuers[index].CreatedAt);
            issuers[index] = updated;
            await WriteAtomicAsync(issuers, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var issuers = (await ReadUnsafeAsync(ct)).ToList();
            var removed = issuers.RemoveAll(issuer => issuer.Id == id);
            if (removed == 0) return false;
            await WriteAtomicAsync(issuers, ct);
            logger.LogInformation("Deleted external ACME issuer {IssuerId}", id);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<ExternalAcmeIssuer>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<ExternalAcmeIssuer>>(stream, cancellationToken: ct) ?? [];
    }

    private async Task WriteAtomicAsync<T>(T value, CancellationToken ct)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }
}

public sealed record CreateExternalAcmeIssuerRequest(string Name, string DirectoryUrl, string ConnectorId);
public sealed record ExternalAcmeIssuer(string Id, string Name, string DirectoryUrl, string ConnectorId, DateTimeOffset CreatedAt);
