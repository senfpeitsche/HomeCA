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

## Benutzerdefinierte CA-URL (Directory-URL)

Viele ACME-Clients (z. B. Certbot, acme.sh, Caddy, Traefik, win-acme, OPNsense) erlauben die Angabe einer benutzerdefinierten CA-URL anstelle von Let's Encrypt. Die URL setzt sich aus der öffentlichen Adresse der HomeCA-Instanz und dem Directory-Pfad zusammen.

HomeCA bietet zwei ACME-Schnittstellen:

| Schnittstelle | Pfad | Zweck |
| --- | --- | --- |
| **RFC 8555** (Standard) | `/acme/directory` | Für alle gängigen ACME-Clients (acme.sh, Certbot, OPNsense, Caddy, Traefik, win-acme). Spricht das vollständige ACME-Protokoll mit JWS-signierten Requests, Nonces und CSR-basierter Finalisierung. |
| **Vereinfachte API** | `/api/v1/acme/directory` | Für direkte curl/API-Nutzung mit Bearer-Token-Authentifizierung. Verwendet einfaches JSON ohne JWS. Siehe Abschnitt 1. |

### Interner ACME-Server (RFC 8555)

Die Directory-URL für Standard-ACME-Clients lautet:

```
http://<hostname>:<port>/acme/directory
```

**Beispiele** (je nach Setup-Konfiguration):

| Szenario | Directory-URL |
| --- | --- |
| HTTP, Standardport | `http://homeca.lab.example.com:5080/acme/directory` |
| HTTPS nach TLS-Aktivierung | `https://homeca.lab.example.com:5443/acme/directory` |
| Hinter Reverse-Proxy (Port 443) | `https://homeca.lab.example.com/acme/directory` |

Hostname und Port entsprechen der beim Setup gewählten Basis-URL. Nach der TLS-Aktivierung verwende die Zieladresse, zu der die HomeCA-Weboberfläche automatisch wechselt; bei der Standardkonfiguration ist das `https://<hostname>:5443`.

### Konfigurationsbeispiele für gängige ACME-Clients

**Certbot:**

```bash
certbot certonly --server http://homeca.lab.example.com:5080/acme/directory \
  --manual --preferred-challenges dns \
  -d node1.lab.example.com
```

**acme.sh:**

```bash
acme.sh --issue --server http://homeca.lab.example.com:5080/acme/directory \
  -d node1.lab.example.com --dns dns_manual
```

**win-acme:**

```powershell
wacs.exe --baseuri http://homeca.lab.example.com:5080/acme/directory
```

> **Hinweis:** Der RFC-8555-Endpunkt stellt eine `http-01`-Challenge bereit. Sobald der Client diese Challenge bestätigt, markiert HomeCA Challenge und Authorization automatisch als `valid`; eine externe HTTP- oder DNS-Erreichbarkeitsprüfung findet nicht statt. Konfiguriere daher beim Client **HTTP-01**. Die Infrastruktur muss dafür nicht öffentlich erreichbar sein.

### Externer ACME-Aussteller

Für externe Aussteller wird die Directory-URL beim Registrieren des Issuers über `directoryUrl` angegeben. Dies ist die URL der öffentlichen CA, nicht von HomeCA selbst. Gängige Werte:

| CA | Directory-URL |
| --- | --- |
| Let's Encrypt (Produktion) | `https://acme-v2.api.letsencrypt.org/directory` |
| Let's Encrypt (Staging) | `https://acme-staging-v02.api.letsencrypt.org/directory` |
| ZeroSSL | `https://acme.zerossl.com/v2/DV90` |
| Google Trust Services | `https://dv.acme-v02.api.pki.goog/directory` |
| Buypass (Produktion) | `https://api.buypass.com/acme/directory` |
| Buypass (Staging) | `https://api.test4.buypass.no/acme/directory` |

---

## 1. Interner ACME-Server (vereinfachte API)

Die vereinfachte API unter `/api/v1/acme/` arbeitet mit einfachen JSON-Requests und Bearer-Token-Authentifizierung. Sie ist für direkte curl-Aufrufe und Skripte gedacht — nicht für Standard-ACME-Clients wie acme.sh, Certbot oder OPNsense (diese verwenden die RFC 8555-Endpunkte unter `/acme/`, siehe oben).

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
curl -s http://127.0.0.1:5080/api/v1/connector-instances \
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

## 3. OPNsense ACME-Client einrichten

Das OPNsense-Plugin `os-acme-client` nutzt intern `acme.sh`, einen vollwertigen RFC 8555 ACME-Client. HomeCA stellt dafür einen RFC 8555-kompatiblen ACME-Server unter `/acme/directory` bereit. Die Einrichtung erfolgt in drei Schritten: Konto anlegen, Challenge-Typ konfigurieren und Zertifikat erstellen.

### 3.1 Plugin installieren und aktivieren

1. Im OPNsense-Webinterface zu **System > Firmware > Plugins** navigieren.
2. Das Plugin `os-acme-client` suchen und über die **+**-Schaltfläche installieren.
3. Nach der Installation die Seite neu laden, damit der neue Menüpunkt erscheint.
4. Unter **Services > ACME Client > Settings** den Haken bei **Enable Plugin** setzen und speichern. **Auto Renewal** aktiviert lassen — das richtet den Cron-Job für die automatische Erneuerung ein.

### 3.2 ACME-Konto registrieren

1. Unter **Services > ACME Client > Accounts** auf **+ Add** klicken.
2. Felder ausfüllen:

| Feld | Wert |
| --- | --- |
| **Enabled** | angehakt |
| **Name** | Frei wählbar, z. B. `HomeCA` |
| **E-Mail Address** | Kontaktadresse, z. B. `admin@lab.example.com` |
| **ACME CA** | **Custom CA URL** auswählen |
| **Custom CA URL** | `http://homeca.lab.example.com:5080/acme/directory` |

Für einen ACME-Client, dessen Quellnetz nicht in der HomeCA-Allowlist steht, werden **Key Identifier** und **HMAC Key** aus den EAB-Zugangsdaten von HomeCA eingetragen. Ist das Clientnetz allowlisted, bleiben beide Felder leer.

3. Auf **Save** klicken.
4. In der Kontoliste die **Register**-Aktion (Kreispfeil-Symbol) auf der neuen Zeile ausführen. Die Registrierung ist ein separater Schritt — erst wenn sie erfolgreich war, können Zertifikate mit diesem Konto ausgestellt werden.

> **Hinweis:** Die Custom CA URL muss genau die Directory-URL des HomeCA RFC 8555 ACME-Servers sein. Wurde TLS aktiviert, übernimm die Zieladresse, zu der die HomeCA-Weboberfläche wechselt (standardmäßig `https://homeca.lab.example.com:5443/acme/directory`).
>
> **Wichtig:** Nicht die vereinfachte API-URL `/api/v1/acme/directory` verwenden — diese spricht nicht das RFC 8555-Protokoll, das acme.sh erwartet.

### 3.3 Challenge-Typ einrichten

1. Unter **Services > ACME Client > Challenge Types** auf **+ Add** klicken.
2. Felder ausfüllen:

| Feld | Wert |
| --- | --- |
| **Name** | Frei wählbar, z. B. `HomeCA Internal` |
| **Challenge Type** | **HTTP-01** auswählen |
| **HTTP Service** | **OPNsense Web Service (automatic port forward)** |

3. Auf **Save** klicken.

> **Warum HTTP-01?** HomeCA liefert für RFC 8555 eine HTTP-01-Challenge und setzt sie nach der Bestätigung durch den Client automatisch auf `valid`; eine externe Validierung findet nicht statt. HTTP-01 ist daher die passende und einfachste Wahl, weil keine DNS-API-Credentials nötig sind. DNS-01 oder TLS-ALPN-01 passen nicht zu der von HomeCA angebotenen Challenge.

### 3.4 Automation anlegen (optional, aber empfohlen)

Damit OPNsense das neue Zertifikat nach der Ausstellung und bei jeder Erneuerung automatisch übernimmt:

1. Unter **Services > ACME Client > Automations** auf **+ Add** klicken.
2. **Name** vergeben, z. B. `Restart WebUI`.
3. **Type** wählen — je nachdem, wo das Zertifikat verwendet wird:

| Verwendung | Automation-Typ |
| --- | --- |
| OPNsense-Webinterface | **Restart OPNsense Web UI** |
| HAProxy Reverse Proxy | **Restart HAProxy** |
| OpenVPN | **System or Plugin Command** mit passendem Befehl |

4. Auf **Save** klicken.

> Ohne Automation schreibt eine Erneuerung zwar neue Dateien, aber der laufende Dienst serviert weiter das alte Zertifikat aus dem Speicher.

### 3.5 Zertifikat erstellen und ausstellen

1. Unter **Services > ACME Client > Certificates** auf **+ Add** klicken.
2. Felder ausfüllen:

| Feld | Wert |
| --- | --- |
| **Common Name** | Der FQDN des Geräts, z. B. `opnsense.lab.example.com` |
| **Alt Names** | Weitere Hostnamen (optional), z. B. `fw.lab.example.com` |
| **ACME Account** | Das in Schritt 3.2 angelegte Konto (`HomeCA`) |
| **Challenge Type** | Der in Schritt 3.3 angelegte Typ (`HomeCA Internal`) |
| **Auto Renewal** | angehakt |
| **Renewal Interval** | `60` (Standard, kann für HomeCA-Zertifikate höher sein) |
| **Key Length** | `ec-256` (empfohlen) oder `4096` für RSA |
| **Automations** | Die in Schritt 3.4 angelegte Automation |

3. Auf **Save** klicken.
4. In der Zertifikatliste die **Issue**-Aktion (Abspielen-Symbol) auf der neuen Zeile ausführen. Die Ausstellung läuft im Hintergrund — der Status in der Spalte **Status** wechselt nach wenigen Sekunden auf den Endstatus.

### 3.6 Zertifikat dem Dienst zuweisen

Das ausgestellte Zertifikat liegt nun im OPNsense Trust Store, wird aber noch von keinem Dienst verwendet.

**Für das OPNsense-Webinterface:**

1. Unter **System > Settings > Administration** das Protokoll auf **HTTPS** stellen.
2. Im Dropdown **SSL Certificate** das neue Zertifikat auswählen.
3. Speichern — die Weboberfläche startet mit dem neuen Zertifikat neu.

**Für andere Dienste** (HAProxy, OpenVPN, etc.) wird das Zertifikat in der jeweiligen Dienstkonfiguration zugewiesen.

### 3.7 Voraussetzung: HomeCA Root-CA vertrauen

Damit OPNsense und die Clients im Netz dem Zertifikat vertrauen, muss die HomeCA Root-CA als vertrauenswürdige Zertifizierungsstelle importiert werden:

1. Die Root-CA-Datei von HomeCA herunterladen: `GET /api/v1/trust-anchor/pem` liefert das PEM ohne Anmeldung.
2. In OPNsense unter **System > Trust > Authorities** auf **+ Add** klicken.
3. **Method** auf **Import an existing Certificate Authority** setzen und das PEM einfügen.
4. Speichern.

Für Clients im Netz siehe [TRUST-INSTALLATION.md](TRUST-INSTALLATION.md).

### 3.8 Fehlerbehebung

| Problem | Lösung |
| --- | --- |
| Konto-Registrierung schlägt fehl | Custom CA URL prüfen — muss exakt auf `/acme/directory` enden (nicht `/api/v1/acme/directory`). HomeCA muss von OPNsense erreichbar sein. |
| „domain validation failed (http01)" | Auf dem HomeCA-Server die Logs prüfen: `journalctl -u homeca -f`. Häufigste Ursachen: (1) DNS-Name ist nicht unter einer aktiven Ausstellungszone (`internalIssuanceEnabled: true`), (2) der Client hat die HTTP-01-Challenge nicht bestätigt, (3) HomeCA ist von OPNsense nicht erreichbar, (4) OPNsense-CA-Trust fehlt (Root-CA unter System > Trust > Authorities importieren). Die Auftragsdetails in HomeCA zeigen Authorization- und Challenge-Status. |
| Zertifikat-Ausstellung hängt | Unter **Services > ACME Client > Log Files** das acme.sh-Log prüfen. Bei Verbindungsproblemen die Erreichbarkeit von HomeCA testen: `curl -s http://homeca.lab.example.com:5080/acme/directory` |
| Browser zeigt weiterhin altes Zertifikat | Automation fehlt oder nicht dem Zertifikat zugewiesen. Siehe Schritt 3.4. |
| Zertifikat wird nicht als vertrauenswürdig erkannt | HomeCA Root-CA ist nicht importiert. Siehe Schritt 3.7. |
| DNS-Name wird abgelehnt | Der Name muss unter einer aktiven Ausstellungszone liegen (`internalIssuanceEnabled: true`). Siehe Schritt 1.1. |

---

## 4. Tipps zur Betriebsführung

### ACME-Client-Allowlist und EAB

HomeCA kombiniert zwei sichere und einfache Zugangswege fuer den RFC-8555-Endpunkt:

- Ein Client aus einem allowlisteten IP-Netz darf sein ACME-Konto ohne weitere Zugangsdaten anlegen.
- Jeder andere Client muss beim Anlegen des Kontos **External Account Binding (EAB)** mit `HS256` verwenden. Das EAB-Geheimnis wird nur beim Erzeugen oder Rotieren angezeigt.

Die Einstellung ist ueber die authentifizierte Verwaltungs-API erreichbar:

```bash
# Aktuelle Policy ansehen
curl -s -H "Authorization: Bearer $TOKEN" \
  http://homeca.lab.example.com:5080/api/v1/acme/access-policy

# Direkte ACME-Clientnetze erlauben (Adresse oder CIDR)
curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"allowlistedClientNetworks":["192.168.10.25","192.168.20.0/24"]}' \
  http://homeca.lab.example.com:5080/api/v1/acme/access-policy

# EAB-Zugangsdaten einmalig erzeugen bzw. rotieren
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
  http://homeca.lab.example.com:5080/api/v1/acme/access-policy/eab/rotate
```

Trage die Antwort `keyId` und `hmacKey` direkt im ACME-Client ein und behandle den HMAC-Key wie ein Kennwort. Eine Rotation macht den bisherigen EAB-Key sofort ungueltig.

Die Allowlist wertet bewusst die IP der direkten TCP-Verbindung aus, nicht ungesicherte Forwarded-Header. Steht HomeCA hinter einem Reverse Proxy, sollte dessen IP **nicht** pauschal allowlistet werden: Sonst wuerden alle vom Proxy kommenden Clients EAB umgehen. In diesem Aufbau EAB verwenden oder die Zugangskontrolle am Proxy entsprechend restriktiv gestalten.

- **Ablaufwarnungen:** `GET /api/v1/warnings/expiring` liefert Zertifikate, die innerhalb von 30 Tagen ablaufen. Integriere diesen Endpunkt in ein tägliches Monitoring.
- **Backup:** Nach jeder ACME-Einrichtung ein verifiziertes Backup erzeugen, siehe [OPERATIONS.md](OPERATIONS.md).
- **Zonenänderungen:** Wird eine Zone nachträglich auf `internalIssuanceEnabled: false` gesetzt, lehnt der ACME-Server neue Orders für diese Zone ab. Bestehende Zertifikate bleiben gültig.
- **Connector-Test:** Führe nach dem Anlegen eines DNS-Connectors den Berechtigungs- und TXT-Test über die API aus, bevor du einen externen Aussteller zuweist.
- **Token-Sicherheit:** Das Bearer-Token ist ein 32-Byte-Zufallswert mit 12 Stunden Gültigkeit. Speichere es nicht dauerhaft und gib es nicht an Dritte weiter.
- **RFC-8555-Diagnose:** In der ACME-Ansicht öffnen **Details** bei einem Auftrag oder Konto. Aufträge zeigen IDs, Ablaufzeit, Authorizationen und Challenge-Status; Konten zeigen Fingerabdruck, Kontakt und zugeordnete Aufträge. Challenge-Tokens werden nicht angezeigt.
