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
    // Two first orders for the same issuer would otherwise both register an account and the
    // second key file would overwrite the first.
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Requests a certificate from an external ACME CA using DNS-01 validation.</summary>
    public async Task<ExternalAcmeResult> RequestCertificateAsync(ExternalAcmeOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IssuerId)) throw new ArgumentException("An external ACME issuer is required.");
        if (request.DnsNames.Count == 0) throw new ArgumentException("At least one DNS name is required.");
        if (request.DnsNames.Any(name => name.Contains('*'))) throw new ArgumentException("Wildcard certificates are not supported.");
        if (request.PropagationDelaySeconds is < 0 or > 600) throw new ArgumentException("The DNS propagation delay must be between 0 and 600 seconds.");

        try
        {
            return await RequestCertificateCoreAsync(request, ct);
        }
        catch (AcmeException exception)
        {
            // Certes reports protocol errors with its own exception types; surface them as a
            // conflict with the server's explanation instead of an opaque 500.
            logger.LogWarning(exception, "External ACME order for {DnsNames} was rejected", string.Join(", ", request.DnsNames));
            throw new InvalidOperationException($"The ACME server rejected the request: {exception.Message}", exception);
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(exception, "External ACME order for {DnsNames} timed out", string.Join(", ", request.DnsNames));
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private async Task<ExternalAcmeResult> RequestCertificateCoreAsync(ExternalAcmeOrderRequest request, CancellationToken ct)
    {

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
                var reasons = new List<string>();
                foreach (var record in challengeRecords)
                {
                    var challenge = await record.Challenge.Resource();
                    if (challenge.Status != ChallengeStatus.Invalid) continue;
                    var reason = challenge.Error?.Detail ?? challenge.Error?.Type ?? "no reason given";
                    logger.LogWarning("DNS-01 challenge for {RecordName} is invalid: {Reason}", record.RecordName, reason);
                    reasons.Add($"{record.RecordName}: {reason}");
                }
                throw new InvalidOperationException(DescribeFailedValidation(reasons));
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
            await CleanupTxtRecordsAsync(dnsImplementation, dnsSettings, challengeRecords.Select(record => (record.RecordName, record.Value)).ToList());
        }
    }

    internal static string DescribeFailedValidation(IReadOnlyList<string> reasons) => reasons.Count == 0
        ? "ACME order validation failed. Check DNS records and try again."
        : $"ACME order validation failed: {string.Join("; ", reasons)}";

    /// <summary>
    /// Removes challenge TXT records. Deliberately ignores the request token: when the client
    /// disconnects or the order fails, the records must still be removed.
    /// </summary>
    internal async Task CleanupTxtRecordsAsync(IDnsConnector dns, ConnectorSettings settings, IReadOnlyList<(string RecordName, string Value)> records)
    {
        using var timeout = new CancellationTokenSource(CleanupTimeout);
        foreach (var (recordName, value) in records)
        {
            try
            {
                logger.LogInformation("Cleaning up TXT record {RecordName}", recordName);
                await dns.DeleteTxtRecordAsync(settings, recordName, value, timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up TXT record {RecordName}; remove it manually", recordName);
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
                try
                {
                    var metadata = JsonSerializer.Deserialize<ExternalAcmeOrderMetadata>(File.ReadAllText(metadataPath));
                    return metadata is null ? null : new ExternalAcmeCertificateItem(metadata.Id, metadata.DnsNames, metadata.IssuerName, metadata.CreatedAt, metadata.OutputPath);
                }
                catch (JsonException exception)
                {
                    // One damaged entry must not hide all other certificates.
                    logger.LogWarning(exception, "Skipping unreadable external certificate metadata {Path}", metadataPath);
                    return null;
                }
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

        await _accountGate.WaitAsync(ct);
        try
        {
            if (File.Exists(keyPath))
            {
                var existingKey = KeyFactory.FromPem(await File.ReadAllTextAsync(keyPath, ct));
                return new AcmeContext(new Uri(issuer.DirectoryUrl), existingKey);
            }

            // Create new account
            var acme = new AcmeContext(new Uri(issuer.DirectoryUrl));
            await acme.NewAccount($"homeca-{issuer.Id}@localhost", true);
            // The account exists at the CA now; never lose its key to a cancelled or torn write.
            var temporaryPath = keyPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, acme.AccountKey.ToPem(), CancellationToken.None);
            File.Move(temporaryPath, keyPath, true);
            logger.LogInformation("Created new ACME account for issuer {IssuerName}", issuer.Name);
            return acme;
        }
        finally { _accountGate.Release(); }
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
