namespace HomeCA.Service.Connectors;

public sealed class TechnitiumDnsConnectorStub : IDnsConnector
{
    public string Type => "technitium";
    public Task<ConnectorCheckResult> CheckAsync(ConnectorSettings settings, CancellationToken cancellationToken) => Task.FromResult(new ConnectorCheckResult(false, [], "Configure the Technitium API endpoint and token before testing."));
    public Task UpsertTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken) => throw new NotImplementedException("Technitium API credentials are not configured.");
    public Task DeleteTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken) => throw new NotImplementedException("Technitium API credentials are not configured.");
}

public sealed class HetznerDnsConnector : IDnsConnector
{
    public string Type => "hetzner";
    public Task<ConnectorCheckResult> CheckAsync(ConnectorSettings settings, CancellationToken cancellationToken) => Task.FromResult(new ConnectorCheckResult(false, [], "Configure the Hetzner DNS API token before testing."));
    public Task UpsertTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken) => throw new NotImplementedException("Hetzner DNS API credentials are not configured.");
    public Task DeleteTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken) => throw new NotImplementedException("Hetzner DNS API credentials are not configured.");
}
