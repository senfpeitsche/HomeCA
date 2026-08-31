# Eigenen DNS-Connector hinzufügen

HomeCA nutzt DNS-Connectoren, um TXT-Records für die ACME-DNS-01-Validierung zu setzen und zu entfernen. Aktuell sind Hetzner DNS und Technitium eingebaut. Das Plugin-System ist so gestaltet, dass ein neuer Connector mit einer einzigen Klasse und einer Zeile DI-Registrierung integriert werden kann.

---

## Architektur im Überblick

```
IDnsConnector                     ← Interface, das jeder Connector implementiert
  ├── HetznerDnsConnector         ← Eingebauter Connector
  ├── TechnitiumDnsConnector      ← Eingebauter Connector
  └── MeinConnector               ← Dein neuer Connector

ConnectorCatalog                  ← Sammelt alle IDnsConnector-Implementierungen per DI
ConnectorRegistry                 ← Speichert konfigurierte Instanzen (Credentials) in JSON
```

`ConnectorCatalog` erhält alle registrierten `IDnsConnector`-Implementierungen automatisch über `IEnumerable<IDnsConnector>` und stellt sie der UI und den API-Endpunkten zur Verfügung. Du musst den Catalog nicht anfassen.

---

## Schritt 1 — Interface verstehen

Das Interface befindet sich in `src/HomeCA.Service/Connectors/IDnsConnector.cs`:

```csharp
public interface IDnsConnector
{
    string Type { get; }
    Task<ConnectorCheckResult> CheckAsync(ConnectorSettings settings, CancellationToken cancellationToken);
    Task UpsertTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken);
    Task DeleteTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken);
}
```

| Member | Aufgabe |
|---|---|
| `Type` | Eindeutiger Bezeichner in Kleinbuchstaben, z. B. `"cloudflare"`. Wird in der UI als Connector-Typ angezeigt und in der Datenbank gespeichert. |
| `CheckAsync` | Prüft die Verbindung mit den übergebenen Zugangsdaten. Gibt `ConnectorCheckResult` mit Status, DNS-Zonen und optionaler Fehlermeldung zurück. |
| `UpsertTxtRecordAsync` | Erstellt oder aktualisiert einen TXT-Record. `recordName` ist der vollqualifizierte DNS-Name (z. B. `_acme-challenge.example.com`), `value` der Inhalt. |
| `DeleteTxtRecordAsync` | Löscht den TXT-Record. Sollte tolerieren, wenn der Record bereits entfernt wurde (kein Fehler bei 404). |

### Hilfstypen

```csharp
// Wird bei jedem Methodenaufruf übergeben
public sealed record ConnectorSettings(
    string Name,                                    // Anzeigename der Instanz
    string Type,                                    // Connector-Typ
    IReadOnlyDictionary<string, string> Secrets      // Provider-spezifische Zugangsdaten
);

// Rückgabe von CheckAsync
public sealed record ConnectorCheckResult(
    bool Connected,                                 // true = Verbindung erfolgreich
    IReadOnlyList<string> Zones,                    // Gefundene DNS-Zonen
    string? Message = null                          // Fehlermeldung bei Misserfolg
);
```

Die `Secrets` sind ein freies String-Dictionary. Jeder Connector definiert selbst, welche Schlüssel er braucht:

| Connector | Secret-Schlüssel |
|---|---|
| Hetzner | `apiToken` |
| Technitium | `endpoint`, `apiKey` |

---

## Schritt 2 — Connector implementieren

Erstelle eine neue Datei unter `src/HomeCA.Service/Connectors/`, z. B. `CloudflareDnsConnector.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace HomeCA.Service.Connectors;

public sealed class CloudflareDnsConnector(
    IHttpClientFactory clients,
    ILogger<CloudflareDnsConnector> logger) : IDnsConnector
{
    public string Type => "cloudflare";

    public async Task<ConnectorCheckResult> CheckAsync(
        ConnectorSettings settings, CancellationToken cancellationToken)
    {
        if (!TryGetSettings(settings, out var apiToken, out var message))
            return new ConnectorCheckResult(false, [], message);

        try
        {
            var client = CreateClient(apiToken);
            using var response = await client.GetAsync("zones", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new ConnectorCheckResult(false, [],
                    $"Cloudflare returned {(int)response.StatusCode}.");

            var body = await response.Content
                .ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var zones = body.GetProperty("result")
                .EnumerateArray()
                .Select(z => z.GetProperty("name").GetString())
                .OfType<string>()
                .ToList();

            return new ConnectorCheckResult(true, zones);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Cloudflare connectivity check failed");
            return new ConnectorCheckResult(false, [],
                $"Could not reach Cloudflare API: {ex.Message}");
        }
    }

    public async Task UpsertTxtRecordAsync(
        ConnectorSettings settings, string recordName,
        string value, CancellationToken cancellationToken)
    {
        if (!TryGetSettings(settings, out var apiToken, out var message))
            throw new InvalidOperationException(message);

        var client = CreateClient(apiToken);
        var zoneId = await FindZoneIdAsync(client, recordName, cancellationToken);

        using var response = await client.PostAsJsonAsync(
            $"zones/{zoneId}/dns_records",
            new { type = "TXT", name = recordName, content = value, ttl = 60 },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        logger.LogInformation("Created TXT record {Record} via Cloudflare", recordName);
    }

    public async Task DeleteTxtRecordAsync(
        ConnectorSettings settings, string recordName,
        string value, CancellationToken cancellationToken)
    {
        if (!TryGetSettings(settings, out var apiToken, out var message))
            throw new InvalidOperationException(message);

        var client = CreateClient(apiToken);
        var zoneId = await FindZoneIdAsync(client, recordName, cancellationToken);

        // Record-ID suchen
        using var list = await client.GetAsync(
            $"zones/{zoneId}/dns_records?type=TXT&name={Uri.EscapeDataString(recordName)}",
            cancellationToken);
        list.EnsureSuccessStatusCode();
        var body = await list.Content
            .ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        foreach (var record in body.GetProperty("result").EnumerateArray())
        {
            if (record.GetProperty("content").GetString() != value) continue;
            var recordId = record.GetProperty("id").GetString();
            using var del = await client.DeleteAsync(
                $"zones/{zoneId}/dns_records/{recordId}", cancellationToken);
            // 404 tolerieren — Record könnte bereits gelöscht sein
            if (del.StatusCode != System.Net.HttpStatusCode.NotFound)
                del.EnsureSuccessStatusCode();
        }
        logger.LogInformation("Deleted TXT record {Record} via Cloudflare", recordName);
    }

    // ── Hilfsmethoden ───────────────────────────────────────────────────────

    private HttpClient CreateClient(string apiToken)
    {
        var client = clients.CreateClient();
        client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiToken);
        return client;
    }

    private static async Task<string> FindZoneIdAsync(
        HttpClient client, string recordName, CancellationToken ct)
    {
        var zones = await client.GetFromJsonAsync<JsonElement>("zones", ct);
        return zones.GetProperty("result")
            .EnumerateArray()
            .Where(z =>
            {
                var name = z.GetProperty("name").GetString() ?? "";
                return recordName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || recordName.EndsWith('.' + name, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(z => (z.GetProperty("name").GetString() ?? "").Length)
            .Select(z => z.GetProperty("id").GetString())
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No Cloudflare zone found for {recordName}.");
    }

    private static bool TryGetSettings(
        ConnectorSettings settings, out string apiToken, out string message)
    {
        apiToken = settings.Secrets.GetValueOrDefault("apiToken", string.Empty);
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            message = "A Cloudflare API token is required.";
            return false;
        }
        message = string.Empty;
        return true;
    }
}
```

### Muster, das sich bewährt hat

- **`TryGetSettings`** als private Hilfsmethode: extrahiert und validiert die Secrets. In `CheckAsync` gibt sie bei Fehlern ein `ConnectorCheckResult(false, …)` zurück, in `Upsert`/`Delete` wirft sie eine `InvalidOperationException`.
- **`IHttpClientFactory`** per Primary Constructor injizieren — nie `new HttpClient()` direkt.
- **`ILogger<T>`** für strukturiertes Logging bei Erfolg und Fehler.
- **Zone-Suche**: Den längsten passenden Zonennamen bevorzugen (longest suffix match), damit Subzonen korrekt aufgelöst werden.
- **404 tolerieren** in `DeleteTxtRecordAsync` — der Record könnte bereits gelöscht sein.

---

## Schritt 3 — DI-Registrierung

Öffne `src/HomeCA.Service/Program.cs` und füge eine Zeile im Bereich der Connector-Registrierungen hinzu:

```csharp
builder.Services.AddSingleton<IDnsConnector, TechnitiumDnsConnector>();
builder.Services.AddSingleton<IDnsConnector, HetznerDnsConnector>();
builder.Services.AddSingleton<IDnsConnector, CloudflareDnsConnector>();  // ← neu
```

Das ist alles. `ConnectorCatalog` sammelt automatisch alle `IDnsConnector`-Registrierungen ein. Der neue Typ erscheint sofort:
- In der UI im Dropdown „Typ" beim Anlegen eines DNS-Connectors
- Im API-Endpunkt `GET /api/v1/connectors`

---

## Schritt 4 — UI-Anpassung (optional)

Die Connector-UI in `Home.razor` zeigt aktuell Felder abhängig vom Typ an. Wenn dein Connector andere Felder als `apiToken` braucht (z. B. einen Endpoint wie Technitium), musst du das Formular erweitern:

```razor
@if (_connectorType == "technitium") {
    <MudTextField @bind-Value="_connectorEndpoint"
                  Label="Technitium-Endpunkt" Required="true" />
}
@if (_connectorType == "cloudflare") {
    <MudTextField @bind-Value="_connectorSecret"
                  Label="Cloudflare API-Token" InputType="InputType.Password"
                  Required="true" />
}
```

Für Connectoren, die nur einen einzelnen API-Token brauchen (wie Hetzner und Cloudflare), funktioniert das bestehende Formular ohne Änderung — das Feld „API-Token" wird zum Secret-Schlüssel `apiToken` gemappt.

Wenn dein Connector zusätzliche Secrets braucht, erweitere die `secrets`-Erstellung in der `AddConnector`-Methode:

```csharp
var secrets = _connectorType switch
{
    "hetzner"    => new Dictionary<string,string> { ["apiToken"] = _connectorSecret },
    "technitium" => new Dictionary<string,string> { ["endpoint"] = _connectorEndpoint,
                                                     ["apiKey"]   = _connectorSecret },
    "cloudflare" => new Dictionary<string,string> { ["apiToken"] = _connectorSecret },
    _            => new Dictionary<string,string> { ["apiToken"] = _connectorSecret }
};
```

---

## Schritt 5 — Testen

1. **Build prüfen:**
   ```bash
   dotnet build
   ```

2. **Connector in der UI anlegen:**
   - Einstellungen > DNS-Connector anlegen > Typ auswählen > Credentials eingeben > Speichern

3. **Verbindung prüfen:**
   - Auf den Button „Prüfen" des Connectors klicken — `CheckAsync` wird aufgerufen und zeigt Zonen oder Fehler an.

4. **TXT-Roundtrip testen:**
   - Mindestens eine Domain anlegen, dann „TXT-Test" klicken — erstellt und löscht sofort einen TXT-Record.

5. **ACME-Zyklus:**
   - Externen ACME-Issuer mit dem neuen Connector anlegen und ein Zertifikat bestellen. Der DNS-01-Challenge-Flow nutzt `UpsertTxtRecordAsync` und `DeleteTxtRecordAsync`.

---

## Checkliste

- [ ] Neue Datei unter `Connectors/` mit Klasse, die `IDnsConnector` implementiert
- [ ] `Type`-Property auf eindeutigen Kleinbuchstaben-Bezeichner gesetzt
- [ ] Benötigte Secret-Schlüssel dokumentiert (in dieser Datei und im Code)
- [ ] `CheckAsync` gibt `ConnectorCheckResult` mit Zonen und Fehlermeldungen zurück
- [ ] `UpsertTxtRecordAsync` erstellt/aktualisiert TXT-Records
- [ ] `DeleteTxtRecordAsync` entfernt TXT-Records und toleriert fehlende Records
- [ ] `IHttpClientFactory` statt `new HttpClient()` verwendet
- [ ] DI-Registrierung in `Program.cs` als `AddSingleton<IDnsConnector, …>()`
- [ ] `dotnet build` und `dotnet test` erfolgreich
- [ ] Manueller Test: Connector anlegen, prüfen, TXT-Roundtrip
