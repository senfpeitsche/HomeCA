namespace HomeCA.Service.Connectors;

public interface IDnsConnector
{
    string Type { get; }
    Task<ConnectorCheckResult> CheckAsync(ConnectorSettings settings, CancellationToken cancellationToken);
    Task UpsertTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken);
    Task DeleteTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken);
}

public sealed record ConnectorSettings(string Name, string Type, IReadOnlyDictionary<string, string> Secrets);
public sealed record ConnectorCheckResult(bool Connected, IReadOnlyList<string> Zones, string? Message = null);

public sealed class ConnectorCatalog(IEnumerable<IDnsConnector> connectors)
{
    public IReadOnlyList<string> Types => connectors.Select(connector => connector.Type).Order().ToList();
    public IDnsConnector? Find(string type) => connectors.FirstOrDefault(connector => connector.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
}
