using System.Text.Json;

namespace HomeCA.Service.Connectors;

public sealed class TechnitiumDnsConnector(IHttpClientFactory clients) : IDnsConnector
{
    public string Type => "technitium";

    public async Task<ConnectorCheckResult> CheckAsync(ConnectorSettings settings, CancellationToken ct)
    {
        if (!TryGetSettings(settings, out var endpoint, out var token, out var message)) return new ConnectorCheckResult(false, [], message);
        using var response = await clients.CreateClient().GetAsync($"{endpoint}/api/zones/list?token={Uri.EscapeDataString(token)}", ct);
        if (!response.IsSuccessStatusCode) return new ConnectorCheckResult(false, [], $"Technitium returned {(int)response.StatusCode}.");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (!payload.RootElement.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase)) return new ConnectorCheckResult(false, [], "Technitium rejected the request.");
        var zones = payload.RootElement.TryGetProperty("response", out var body) && body.TryGetProperty("zones", out var entries) ? entries.EnumerateArray().Select(zone => zone.TryGetProperty("name", out var name) ? name.GetString() : null).OfType<string>().ToList() : [];
        return new ConnectorCheckResult(true, zones);
    }

    public async Task UpsertTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken ct)
    {
        if (!TryGetSettings(settings, out var endpoint, out var token, out var message)) throw new InvalidOperationException(message);
        using var response = await clients.CreateClient().GetAsync($"{endpoint}/api/zones/records/add?token={Uri.EscapeDataString(token)}&domain={Uri.EscapeDataString(recordName)}&type=TXT&ttl=60&text={Uri.EscapeDataString(value)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken ct)
    {
        if (!TryGetSettings(settings, out var endpoint, out var token, out var message)) throw new InvalidOperationException(message);
        using var response = await clients.CreateClient().GetAsync($"{endpoint}/api/zones/records/delete?token={Uri.EscapeDataString(token)}&domain={Uri.EscapeDataString(recordName)}&type=TXT&text={Uri.EscapeDataString(value)}", ct);
        response.EnsureSuccessStatusCode();
    }

    private static bool TryGetSettings(ConnectorSettings settings, out string endpoint, out string token, out string message)
    {
        endpoint = settings.Secrets.GetValueOrDefault("endpoint", string.Empty).TrimEnd('/');
        token = settings.Secrets.GetValueOrDefault("apiKey", string.Empty);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            message = "A Technitium endpoint is required.";
            return false;
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            message = "The Technitium endpoint must be an absolute URL, for example http://192.168.1.10:5380.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            message = "A Technitium API key is required.";
            return false;
        }
        message = string.Empty;
        return true;
    }
}
