using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Acme;

/// <summary>Stores external ACME directories as managed issuer configuration; DNS-01 execution is delegated to a DNS connector.</summary>
public sealed class ExternalAcmeIssuerRegistry(HomeCaStorage storage)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "external-acme-issuers.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<ExternalAcmeIssuer>> ListAsync(CancellationToken ct) => File.Exists(_path) ? await JsonSerializer.DeserializeAsync<List<ExternalAcmeIssuer>>(File.OpenRead(_path), cancellationToken: ct) ?? [] : [];

    public async Task<ExternalAcmeIssuer> AddAsync(CreateExternalAcmeIssuerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ConnectorId)) throw new ArgumentException("Issuer name and DNS connector instance are required.");
        if (!Uri.TryCreate(request.DirectoryUrl, UriKind.Absolute, out var directory) || directory.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A valid HTTPS directory URL is required.");
        await _gate.WaitAsync(ct);
        try
        {
            var issuers = (await ListAsync(ct)).ToList();
            if (issuers.Any(issuer => issuer.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("An issuer with this name already exists.");
            var issuer = new ExternalAcmeIssuer(Guid.NewGuid().ToString("N"), request.Name.Trim(), directory.AbsoluteUri, request.ConnectorId.Trim(), DateTimeOffset.UtcNow);
            issuers.Add(issuer);
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, issuers, cancellationToken: ct);
            return issuer;
        }
        finally { _gate.Release(); }
    }
}

public sealed record CreateExternalAcmeIssuerRequest(string Name, string DirectoryUrl, string ConnectorId);
public sealed record ExternalAcmeIssuer(string Id, string Name, string DirectoryUrl, string ConnectorId, DateTimeOffset CreatedAt);
