using System.Text.Json;

namespace HomeCA.Service.Connectors;

public sealed class TechnitiumDnsConnectorStub : IDnsConnector
{
    public string Type => "technitium";
    public Task<ConnectorCheckResult> CheckAsync(ConnectorSettings settings, CancellationToken cancellationToken) => Task.FromResult(new ConnectorCheckResult(false, [], "Configure the Technitium API endpoint and token before testing."));
    public Task UpsertTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken) => throw new NotImplementedException("Technitium API credentials are not configured.");
    public Task DeleteTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken) => throw new NotImplementedException("Technitium API credentials are not configured.");
}

public sealed class HetznerDnsConnector(IHttpClientFactory clients) : IDnsConnector
{
    public string Type => "hetzner";
    public async Task<ConnectorCheckResult> CheckAsync(ConnectorSettings settings, CancellationToken cancellationToken)
    {
        var client = CreateClient(settings);
        using var response = await client.GetAsync("zones", cancellationToken);
        if (!response.IsSuccessStatusCode) return new ConnectorCheckResult(false, [], $"Hetzner Cloud returned {(int)response.StatusCode}.");
        try
        {
            var zones = await response.Content.ReadFromJsonAsync<ZoneList>(cancellationToken: cancellationToken);
            return new ConnectorCheckResult(true, zones?.Zones.Select(zone => zone.Name).ToList() ?? []);
        }
        catch (JsonException)
        {
            return new ConnectorCheckResult(false, [], "Hetzner Cloud returned an unexpected response. Use a project API token from the Hetzner Cloud Console.");
        }
    }
    public async Task UpsertTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken)
    {
        var client = CreateClient(settings); var zone = await FindZoneAsync(client, recordName, cancellationToken); var rrsetName = ToRelativeName(recordName, zone.Name);
        using var existing = await client.GetAsync($"zones/{zone.Id}/rrsets/{Uri.EscapeDataString(rrsetName)}/TXT", cancellationToken);
        if (existing.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            using var created = await client.PostAsJsonAsync($"zones/{zone.Id}/rrsets", new { name = rrsetName, type = "TXT", ttl = 60, records = new[] { new { value } } }, cancellationToken);
            created.EnsureSuccessStatusCode();
            return;
        }
        existing.EnsureSuccessStatusCode();
        using var added = await client.PostAsJsonAsync($"zones/{zone.Id}/rrsets/{Uri.EscapeDataString(rrsetName)}/TXT/actions/add_records", new { records = new[] { new { value } } }, cancellationToken);
        added.EnsureSuccessStatusCode();
    }
    public async Task DeleteTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken)
    {
        var client = CreateClient(settings); var zone = await FindZoneAsync(client, recordName, cancellationToken); var rrsetName = ToRelativeName(recordName, zone.Name);
        using var response = await client.PostAsJsonAsync($"zones/{zone.Id}/rrsets/{Uri.EscapeDataString(rrsetName)}/TXT/actions/remove_records", new { records = new[] { new { value } } }, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }
    private HttpClient CreateClient(ConnectorSettings settings)
    {
        if (!settings.Secrets.TryGetValue("apiToken", out var token) || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Hetzner API token is required.");
        var client = clients.CreateClient(); client.BaseAddress = new Uri("https://api.hetzner.cloud/v1/"); client.DefaultRequestHeaders.Authorization = new("Bearer", token); return client;
    }
    private static async Task<Zone> FindZoneAsync(HttpClient client, string recordName, CancellationToken ct)
    {
        var zones = (await client.GetFromJsonAsync<ZoneList>("zones", ct))?.Zones ?? [];
        return zones.Where(zone => recordName.Equals(zone.Name, StringComparison.OrdinalIgnoreCase) || recordName.EndsWith('.' + zone.Name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(zone => zone.Name.Length).FirstOrDefault() ?? throw new InvalidOperationException($"No Hetzner zone owns {recordName}.");
    }
    private static string ToRelativeName(string recordName, string zoneName)
    {
        var normalizedRecord = recordName.TrimEnd('.');
        var normalizedZone = zoneName.TrimEnd('.');
        return normalizedRecord.Equals(normalizedZone, StringComparison.OrdinalIgnoreCase) ? "@" : normalizedRecord[..^(normalizedZone.Length + 1)];
    }
    private sealed record ZoneList(IReadOnlyList<Zone> Zones); private sealed record Zone(long Id, string Name);
}
