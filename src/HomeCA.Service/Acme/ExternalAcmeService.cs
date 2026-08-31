using System.Text.Json;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using HomeCA.Service.Connectors;
using HomeCA.Service.Infrastructure;
using IODirectory = System.IO.Directory;

namespace HomeCA.Service.Acme;

/// <summary>
/// Executes the full ACME workflow against external CAs (e.g. Let's Encrypt) using DNS-01 challenges
/// via a registered DNS connector. Manages ACME account keys and persists them for reuse.
/// </summary>
public sealed class ExternalAcmeService(
    HomeCaStorage storage,
    ExternalAcmeIssuerRegistry issuers,
    ConnectorRegistry connectors,
    ConnectorCatalog catalog,
    ILogger<ExternalAcmeService> logger)
{
    private readonly string _accountRoot = Path.Combine(storage.RootPath, "state", "acme-accounts");
    private readonly string _externalCertRoot = Path.Combine(storage.RootPath, "external-certificates");

    /// <summary>Requests a certificate from an external ACME CA using DNS-01 validation.</summary>
    public async Task<ExternalAcmeResult> RequestCertificateAsync(ExternalAcmeOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IssuerId)) throw new ArgumentException("An external ACME issuer is required.");
        if (request.DnsNames.Count == 0) throw new ArgumentException("At least one DNS name is required.");
        if (request.DnsNames.Any(name => name.Contains('*'))) throw new ArgumentException("Wildcard certificates are not supported.");

        var issuer = (await issuers.ListAsync(ct)).FirstOrDefault(i => i.Id == request.IssuerId)
            ?? throw new ArgumentException($"External ACME issuer '{request.IssuerId}' not found.");

        var connector = await connectors.GetAsync(issuer.ConnectorId, ct)
            ?? throw new InvalidOperationException($"DNS connector '{issuer.ConnectorId}' not found.");
        var dnsImplementation = catalog.Find(connector.Type)
            ?? throw new InvalidOperationException($"DNS connector type '{connector.Type}' is not available.");
        var dnsSettings = new ConnectorSettings(connector.Name, connector.Type, connector.Secrets);

        // Get or create ACME context with persisted account key
        var acme = await GetOrCreateAcmeContextAsync(issuer, ct);

        logger.LogInformation("Creating ACME order for {DnsNames} via {Issuer} ({DirectoryUrl})",
            string.Join(", ", request.DnsNames), issuer.Name, issuer.DirectoryUrl);

        // Create order
        var dnsNamesList = request.DnsNames.ToList();
        var order = await acme.NewOrder(dnsNamesList);

        // Process DNS-01 challenges
        var authorizations = await order.Authorizations();
        var challengeRecords = new List<(string RecordName, string Value, IChallengeContext Challenge)>();

        try
        {
            foreach (var authz in authorizations)
            {
                var dns01 = await authz.Dns();
                if (dns01 is null) throw new InvalidOperationException("The ACME server did not offer a DNS-01 challenge.");

                var resource = await authz.Resource();
                var identifier = resource.Identifier.Value;
                var recordName = $"_acme-challenge.{identifier}";
                var txtValue = acme.AccountKey.DnsTxt(dns01.Token);

                logger.LogInformation("Setting TXT record {RecordName} = {Value}", recordName, txtValue);
                await dnsImplementation.UpsertTxtRecordAsync(dnsSettings, recordName, txtValue, ct);
                challengeRecords.Add((recordName, txtValue, dns01));
            }

            // Wait for DNS propagation
            logger.LogInformation("Waiting {Seconds}s for DNS propagation", request.PropagationDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(request.PropagationDelaySeconds), ct);

            // Validate all challenges
            foreach (var record in challengeRecords)
            {
                var validation = await record.Challenge.Validate();
                logger.LogInformation("Challenge validation status: {Status}", validation.Status);
            }

            // Wait for validation to complete
            var orderResource = await WaitForOrderAsync(order, ct);
            if (orderResource.Status == OrderStatus.Invalid)
            {
                throw new InvalidOperationException("ACME order validation failed. Check DNS records and try again.");
            }

            // Generate CSR and finalize
            var csrKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var primaryDns = request.DnsNames[0];
            var certChain = await order.Generate(new CsrInfo { CommonName = primaryDns }, csrKey);

            // Save certificate files
            var id = Guid.NewGuid().ToString("N");
            var outputPath = Path.Combine(_externalCertRoot, id);
            IODirectory.CreateDirectory(outputPath);

            // PEM with full chain
            var pemChain = certChain.ToPem();
            await File.WriteAllTextAsync(Path.Combine(outputPath, "fullchain.pem"), pemChain, ct);

            // Private key
            var keyPem = csrKey.ToPem();
            await File.WriteAllTextAsync(Path.Combine(outputPath, "key.pem"), keyPem, ct);

            // Individual cert (first in chain)
            var certPem = certChain.Certificate.ToPem();
            await File.WriteAllTextAsync(Path.Combine(outputPath, "certificate.pem"), certPem, ct);

            // Save metadata
            var metadata = new ExternalAcmeOrderMetadata(id, issuer.Name, request.DnsNames, DateTimeOffset.UtcNow, outputPath);
            await File.WriteAllTextAsync(Path.Combine(outputPath, "metadata.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), ct);

            logger.LogInformation("External ACME certificate obtained: {Id} for {DnsNames}", id, string.Join(", ", request.DnsNames));

            return new ExternalAcmeResult(id, primaryDns, request.DnsNames, issuer.Name, outputPath);
        }
        finally
        {
            // Always clean up DNS records
            foreach (var record in challengeRecords)
            {
                try
                {
                    logger.LogInformation("Cleaning up TXT record {RecordName}", record.RecordName);
                    await dnsImplementation.DeleteTxtRecordAsync(dnsSettings, record.RecordName, record.Value, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to clean up TXT record {RecordName}", record.RecordName);
                }
            }
        }
    }

    /// <summary>Lists certificates obtained from external ACME CAs.</summary>
    public Task<IReadOnlyList<ExternalAcmeCertificateItem>> ListCertificatesAsync(CancellationToken ct)
    {
        if (!IODirectory.Exists(_externalCertRoot))
            return Task.FromResult<IReadOnlyList<ExternalAcmeCertificateItem>>([]);

        var items = IODirectory.EnumerateDirectories(_externalCertRoot)
            .Select(dir =>
            {
                var metadataPath = Path.Combine(dir, "metadata.json");
                if (!File.Exists(metadataPath)) return null;
                var metadata = JsonSerializer.Deserialize<ExternalAcmeOrderMetadata>(File.ReadAllText(metadataPath));
                return metadata is null ? null : new ExternalAcmeCertificateItem(metadata.Id, metadata.DnsNames, metadata.IssuerName, metadata.CreatedAt, metadata.OutputPath);
            })
            .Where(item => item is not null)
            .Cast<ExternalAcmeCertificateItem>()
            .OrderByDescending(item => item.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ExternalAcmeCertificateItem>>(items);
    }

    private async Task<AcmeContext> GetOrCreateAcmeContextAsync(ExternalAcmeIssuer issuer, CancellationToken ct)
    {
        IODirectory.CreateDirectory(_accountRoot);
        var keyPath = Path.Combine(_accountRoot, $"{issuer.Id}.pem");

        if (File.Exists(keyPath))
        {
            var existingKey = KeyFactory.FromPem(await File.ReadAllTextAsync(keyPath, ct));
            return new AcmeContext(new Uri(issuer.DirectoryUrl), existingKey);
        }

        // Create new account
        var acme = new AcmeContext(new Uri(issuer.DirectoryUrl));
        await acme.NewAccount($"homeca-{issuer.Id}@localhost", true);
        await File.WriteAllTextAsync(keyPath, acme.AccountKey.ToPem(), ct);
        logger.LogInformation("Created new ACME account for issuer {IssuerName}", issuer.Name);
        return acme;
    }

    private static async Task<Order> WaitForOrderAsync(IOrderContext order, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var resource = await order.Resource();
            if (resource.Status is OrderStatus.Ready or OrderStatus.Valid or OrderStatus.Invalid)
                return resource;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException("ACME order did not complete within the expected time.");
    }
}

public sealed record ExternalAcmeOrderRequest(string IssuerId, IReadOnlyList<string> DnsNames, int PropagationDelaySeconds = 30);
public sealed record ExternalAcmeResult(string Id, string Subject, IReadOnlyList<string> DnsNames, string IssuerName, string ExportPath);
public sealed record ExternalAcmeCertificateItem(string Id, IReadOnlyList<string> DnsNames, string IssuerName, DateTimeOffset CreatedAt, string ExportPath);
public sealed record ExternalAcmeOrderMetadata(string Id, string IssuerName, IReadOnlyList<string> DnsNames, DateTimeOffset CreatedAt, string OutputPath);
