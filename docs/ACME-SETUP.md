# ACME-Einrichtung

HomeCA bietet zwei ACME-Betriebsarten: einen internen ACME-Server, der Zertifikate über die eigene TLS Issuing CA ausstellt, und eine verwaltete Registrierung für externe ACME-Aussteller (z. B. Let's Encrypt), die DNS-01-Challenges über einen konfigurierten DNS-Connector abwickeln.

## Voraussetzungen

- HomeCA ist installiert und der Healthcheck unter `/health` antwortet erfolgreich.
- Der Administrator wurde über den lokalen Setup-Endpunkt eingerichtet.
- Root- und Issuing-CA sind initialisiert (`POST /api/v1/authorities/initialize`).

Alle folgenden Befehle setzen eine gültige Sitzung voraus. Melde dich zuerst an und speichere das Bearer-Token:

```bash
TOKEN=$(curl -s http://127.0.0.1:5080/api/v1/login \
  -H 'Content-Type: application/json' \
  -d '{"userName":"<admin>","password":"<passwort>"}' \
  | jq -r '.accessToken')
```

Das Token ist 12 Stunden gültig.

---

## 1. Interner ACME-Server

Der interne ACME-Server stellt Zertifikate für DNS-Namen aus, die unter einer aktivierten internen Ausstellungszone liegen. Orders gehen direkt in den Status `ready`, da alle Clients als vertrauenswürdig gelten; eine Challenge-Validierung findet nicht statt.

### 1.1 Ausstellungszone anlegen

Lege eine Domain mit aktivierter interner Ausstellung an. Der ACME-Server stellt Zertifikate für alle Namen aus, die exakt der Zone entsprechen oder eine Subdomain davon sind.

```bash
curl -s http://127.0.0.1:5080/api/v1/domains \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"name":"lab.example.com","internalIssuanceEnabled":true,"connectorId":null}'
```

Für mehrere unabhängige Zonen den Aufruf wiederholen. Nur Zonen mit `internalIssuanceEnabled: true` werden vom ACME-Server berücksichtigt.

### 1.2 Directory abrufen

Der Directory-Endpunkt ist ohne Authentifizierung erreichbar und liefert die Einstiegspunkte:

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/directory
```

Antwort:

```json
{
  "newAccount": "/api/v1/acme/accounts",
  "newOrder": "/api/v1/acme/orders",
  "finalizeOrder": "/api/v1/acme/orders/{orderId}/finalize"
}
```

### 1.3 ACME-Account registrieren

Die Account-Registrierung ist ebenfalls ohne Bearer-Token erreichbar und idempotent: bei identischem Kontakt wird der bestehende Account zurückgegeben.

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/accounts \
  -H 'Content-Type: application/json' \
  -d '{"contact":"admin@lab.example.com"}'
```

Antwort:

```json
{
  "id": "a1b2c3...",
  "contact": "admin@lab.example.com",
  "createdAt": "2026-08-31T10:00:00Z"
}
```

Notiere die `id` für die folgenden Schritte.

### 1.4 Order erstellen

Erstelle eine Order für einen oder mehrere DNS-Namen. Alle Namen müssen unter einer aktiven Ausstellungszone liegen.

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/orders \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"accountId":"<account-id>","identifiers":["node1.lab.example.com","node2.lab.example.com"]}'
```

Antwort:

```json
{
  "id": "d4e5f6...",
  "accountId": "a1b2c3...",
  "identifiers": ["node1.lab.example.com", "node2.lab.example.com"],
  "status": "ready",
  "createdAt": "2026-08-31T10:01:00Z",
  "certificateId": null
}
```

### 1.5 Order finalisieren

Die Finalisierung erzeugt das Zertifikat, signiert von der TLS Issuing CA.

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/orders/d4e5f6.../finalize \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"validityDays":365,"keyAlgorithm":"ECC","rsaKeySize":2048}'
```

| Parameter | Standard | Erklärung |
| --- | --- | --- |
| `validityDays` | 365 | 1 bis 730 Tage |
| `keyAlgorithm` | `ECC` | `ECC` (P-256) oder `RSA` |
| `rsaKeySize` | 2048 | Nur bei `RSA`: 2048 oder 3072 |

Bei Erfolg wechselt der Status auf `valid` und die Antwort enthält die `certificateId`. Die exportierten Dateien liegen im Datenverzeichnis unter `exports/<certificateId>/`:

| Datei | Inhalt |
| --- | --- |
| `certificate.pem` | Ausgestelltes Serverzertifikat |
| `chain.pem` | Issuing-CA + Root-CA Kette |

### 1.6 Order-Status prüfen

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/orders/<order-id> \
  -H "Authorization: Bearer $TOKEN"
```

### 1.7 Zertifikat prüfen

Verifiziere die Kette nach der Ausstellung:

```bash
openssl verify -CAfile chain.pem certificate.pem
```

---

## 2. Externe ACME-Aussteller

Externe ACME-Aussteller verweisen auf die Directory-URL einer öffentlichen CA. Die DNS-01-Challenge-Verwaltung wird über den zugewiesenen DNS-Connector abgewickelt.

### 2.1 DNS-Connector einrichten

Richte zuerst eine Connector-Instanz ein, falls noch nicht vorhanden. Unterstützte Typen sind `technitium` und `hetzner`.

```bash
curl -s http://127.0.0.1:5080/api/v1/connectors \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "name":"Hetzner Prod",
    "type":"hetzner",
    "secrets":{"apiToken":"<hetzner-dns-api-token>"}
  }'
```

Notiere die zurückgegebene `id` des Connectors.

### 2.2 Externen Aussteller registrieren

Registriere den externen ACME-Aussteller mit der Directory-URL der CA und dem Connector für die DNS-01-Challenges:

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/external-issuers \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "name":"Lets Encrypt Production",
    "directoryUrl":"https://acme-v2.api.letsencrypt.org/directory",
    "connectorId":"<connector-id>"
  }'
```

Die `directoryUrl` muss ein gültiger HTTPS-Endpunkt sein. Der Name darf nicht mehrfach vergeben werden.

### 2.3 Registrierte Aussteller auflisten

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/external-issuers \
  -H "Authorization: Bearer $TOKEN"
```

---

## 3. Tipps zur Betriebsführung

- **Ablaufwarnungen:** `GET /api/v1/warnings/expiring` liefert Zertifikate, die innerhalb von 30 Tagen ablaufen. Integriere diesen Endpunkt in ein tägliches Monitoring.
- **Backup:** Nach jeder ACME-Einrichtung ein verifiziertes Backup erzeugen, siehe [OPERATIONS.md](OPERATIONS.md).
- **Zonenänderungen:** Wird eine Zone nachträglich auf `internalIssuanceEnabled: false` gesetzt, lehnt der ACME-Server neue Orders für diese Zone ab. Bestehende Zertifikate bleiben gültig.
- **Connector-Test:** Führe nach dem Anlegen eines DNS-Connectors den Berechtigungs- und TXT-Test über die API aus, bevor du einen externen Aussteller zuweist.
- **Token-Sicherheit:** Das Bearer-Token ist ein 32-Byte-Zufallswert mit 12 Stunden Gültigkeit. Speichere es nicht dauerhaft und gib es nicht an Dritte weiter.
