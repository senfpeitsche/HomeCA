using System.Net.Http.Json;
namespace HomeCA.Service.Connectors;
public sealed class TechnitiumDnsConnector(IHttpClientFactory clients) : IDnsConnector
{
 public string Type=>"technitium";
 public async Task<ConnectorCheckResult> CheckAsync(ConnectorSettings s,CancellationToken ct){ var endpoint=s.Secrets["endpoint"].TrimEnd('/'); var token=s.Secrets["apiKey"]; var r=await clients.CreateClient().GetFromJsonAsync<TechnitiumResponse>($"{endpoint}/api/zones/list?token={Uri.EscapeDataString(token)}",ct); return r?.status=="ok"?new(true,[]):new(false,[],"Technitium rejected the request."); }
 public Task UpsertTxtRecordAsync(ConnectorSettings s,string n,string v,CancellationToken ct)=>throw new NotImplementedException("TXT mutation will be added after zone selection is configured."); public Task DeleteTxtRecordAsync(ConnectorSettings s,string n,string v,CancellationToken ct)=>throw new NotImplementedException(); private sealed record TechnitiumResponse(string status);
}
