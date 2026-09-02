# TLS-Zertifikate ausstellen und einsetzen

Diese Anleitung beschreibt, wie du mit HomeCA TLS- und mTLS-Zertifikate für interne Dienste ausstellst, auf Zielsysteme überträgst und die automatische Erneuerung einrichtest.

## Voraussetzungen

- HomeCA läuft und `/health` antwortet erfolgreich.
- Root- und Issuing-CA sind initialisiert (Weboberfläche > Zertifizierungsstellen > „CA erstellen" oder `POST /api/v1/authorities/initialize`).
- Das Root-CA-Zertifikat ist auf den Clients installiert, die den Zertifikaten vertrauen sollen — siehe [TRUST-INSTALLATION.md](TRUST-INSTALLATION.md).
- `PublicUrl` ist in `appsettings.json` konfiguriert, damit die CRL-Verteilungspunkte (CDP) in die Zertifikate eingebettet werden:

```json
{
  "Storage": {
    "PublicUrl": "http://homeca.int.example.org:5080"
  }
}
```

Alle API-Aufrufe in dieser Anleitung setzen eine gültige Sitzung voraus:

```bash
TOKEN=$(curl -s http://127.0.0.1:5080/api/v1/login \
  -H 'Content-Type: application/json' \
  -d '{"userName":"<admin>","password":"<passwort>"}' \
  | jq -r '.accessToken')
```

---

## 1. Zertifikat über die Weboberfläche ausstellen

Die Weboberfläche führt in drei Schritten durch die Ausstellung:

### Schritt 1 — Zielprofil wählen

Öffne den Bereich **Zertifikate** und wähle im Tab „TLS- und mTLS-Zertifikat" ein Zielprofil aus der Dropdown-Liste. Das Profil setzt Standardwerte für Schlüsselalgorithmus, maximale Laufzeit, Exportformate und IP-SAN-Unterstützung. In der Seitenleiste erscheinen die Profildetails mit Installationshinweisen und Skript-Vorschau.

Verfügbare Profile:

| Profil | Algorithmus | Exportformate | Besonderheiten |
|---|---|---|---|
| Generisches TLS | ECC | PEM, PFX | Universell einsetzbar |
| Windows IIS | RSA | PFX | Für IIS-Bindung und RDP |
| Proxmox VE | ECC | PEM | Kein IP-SAN |
| OPNsense | RSA | PFX, PEM | Firewall-Weboberfläche |
| Home Assistant | ECC | PEM | Kein IP-SAN |
| UniFi OS | ECC | PEM | Kein IP-SAN |
| HAProxy | ECC | PEM | Bundle-Export (Key+Cert+Chain) |
| nginx | ECC | PEM | Fullchain für `ssl_certificate` |
| Cisco Switch | RSA | PEM, PFX | PKCS12-Import per CLI |
| Huawei Switch | RSA | PEM, PFX | VRP-spezifischer Import |
| Synology DSM | RSA | PEM | Kein IP-SAN |
| TeamCity | RSA | PFX, PEM | Java-Keystore oder Reverse Proxy |

### Schritt 2 — Zertifikat definieren

Fülle die Felder aus:

- **Verwendung:** `TLS-Server` für normale Serverzertifikate oder `mTLS / Client` für gegenseitige Authentifizierung. Bei mTLS enthält das Zertifikat sowohl die Server- als auch die Client-Authentifizierungs-EKU.
- **DNS-Namen:** Kommagetrennt, z. B. `app.home.lab, api.home.lab`. Mindestens ein DNS-Name ist für die meisten Profile erforderlich. Wildcards werden unterstützt (`*.home.lab`).
- **IP-Adressen:** Optional und kommagetrennt, z. B. `192.168.1.10, 10.0.0.5`. Nicht bei allen Profilen verfügbar.
- **Gültigkeit in Tagen:** 1 bis maximal 730, abhängig vom Profil.
- **Schlüsselalgorithmus:** ECC (P-256) oder RSA. Einige Profile erzwingen RSA.
- **RSA-Schlüssellänge:** 2048 oder 3072 Bit, nur bei RSA sichtbar.

### Schritt 3 — Ausstellen und herunterladen

Klicke auf den Ausstellungsbutton (z. B. „Proxmox VE ausstellen"). HomeCA erzeugt das Zertifikat, signiert es mit der Issuing CA und erstellt ein Deployment-Paket mit allen Exportdateien.

Das Zertifikat erscheint im **Zertifikatsinventar** mit Download-Buttons:

| Export | Inhalt | Typischer Einsatz |
|---|---|---|
| **PEM** | Serverzertifikat | Standardformat für Linux-Dienste |
| **Key** | Privater Schlüssel (PKCS#8) | Immer zusammen mit PEM verwenden |
| **Chain** | Issuing-CA + Root-CA | Für Dienste, die die Kette separat brauchen |
| **Fullchain** | Zertifikat + Chain | nginx `ssl_certificate`, Apache |
| **Bundle** | Key + Zertifikat + Chain | HAProxy `crt`-Datei (alles in einer Datei) |
| **PFX** | PKCS#12-Archiv mit Kennwort | Windows, Java-Keystores, Cisco |
| **Paket ZIP** | Alle Exporte, Anleitung, Snapshot, Prüfsummen und Erneuerungsskript | Vollständige Übergabe an ein Zielsystem |

Beim PFX-Download wirst du nach einem Kennwort gefragt, mit dem das Archiv verschlüsselt wird.
Das **Paket ZIP** enthält auch den privaten Schlüssel und ist nicht zusätzlich verschlüsselt. Lade es nur über einen vertrauenswürdigen Client herunter und bewahre es wie den privaten Schlüssel selbst auf.

Über den Button **Details** kannst du Subject, Aussteller, Seriennummer, SHA-256-Fingerabdruck, SANs und EKUs des Zertifikats einsehen.

---

## 2. Zertifikat über die API ausstellen

### 2.1 Ausstellung

```bash
curl -s http://127.0.0.1:5080/api/v1/certificates \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "usage": "TLS",
    "dnsNames": ["app.home.lab", "api.home.lab"],
    "ipAddresses": ["192.168.1.10"],
    "validityDays": 365,
    "keyAlgorithm": "ECC",
    "rsaKeySize": 2048,
    "targetProfileId": "generic-tls"
  }'
```

| Parameter | Pflicht | Standard | Erklärung |
|---|---|---|---|
| `usage` | Ja | — | `TLS` oder `mTLS` |
| `dnsNames` | Ja* | — | Liste von DNS-Namen |
| `ipAddresses` | Nein | `[]` | Liste von IP-Adressen |
| `validityDays` | Nein | `365` | 1 bis 730 Tage |
| `keyAlgorithm` | Nein | `ECC` | `ECC` (P-256) oder `RSA` |
| `rsaKeySize` | Nein | `2048` | Nur bei RSA: `2048` oder `3072` |
| `targetProfileId` | Nein | `generic-tls` | Profilbezogenes Deployment-Paket |

*Mindestens ein DNS-Name oder eine IP-Adresse ist erforderlich.

Antwort:

```json
{
  "id": "c41791a284c9dd5f36b6191313d04539",
  "subject": "CN=app.home.lab",
  "expiresAt": "2027-08-31T10:00:00Z",
  "usage": "TLS",
  "keyAlgorithm": "ECC",
  "exportPath": "/var/lib/homeca/exports/c41791a284c9dd5f36b6191313d04539"
}
```

### 2.2 Exportdateien herunterladen

Ersetze `<id>` durch die Zertifikats-ID aus der Antwort:

```bash
# Serverzertifikat
curl -s -o certificate.pem \
  -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5080/api/v1/certificates/<id>/export/pem

# Privater Schlüssel
curl -s -o key.pem \
  -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5080/api/v1/certificates/<id>/export/key

# CA-Kette (Issuing + Root)
curl -s -o chain.pem \
  -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5080/api/v1/certificates/<id>/export/chain

# Fullchain (Zertifikat + Kette)
curl -s -o fullchain.pem \
  -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5080/api/v1/certificates/<id>/export/fullchain

# Bundle (Key + Zertifikat + Kette, z. B. für HAProxy)
curl -s -o bundle.pem \
  -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5080/api/v1/certificates/<id>/export/bundle

# PFX mit Kennwort
curl -s -o certificate.pfx \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"password":"MeinSicheresKennwort"}' \
  http://127.0.0.1:5080/api/v1/certificates/<id>/export/pfx

# Vollständigen Deployment-Snapshot als ZIP herunterladen
# Enthält auch den privaten Schlüssel; nur über einen vertrauenswürdigen Client speichern.
curl -s -o deployment-package.zip \
  -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5080/api/v1/certificates/<id>/export/package
```

Der PFX-Export enthält das ausgestellte Zertifikat samt privatem Schlüssel und Issuing-CA. Die Root-CA wird absichtlich nicht mitgeliefert; sie wird einmalig als Vertrauensanker auf Clients und Geräten installiert.

### 2.3 Zertifikat verifizieren

```bash
# Kette prüfen
openssl verify -CAfile chain.pem certificate.pem

# SANs und Laufzeit anzeigen
openssl x509 -in certificate.pem -noout -text | grep -A1 "Subject Alternative Name"
openssl x509 -in certificate.pem -noout -dates

# Gegen einen laufenden Dienst testen
openssl s_client -connect app.home.lab:443 -CAfile chain.pem
```

### 2.4 Zertifikatsdetails abrufen

```bash
curl -s http://127.0.0.1:5080/api/v1/certificates/<id> \
  -H "Authorization: Bearer $TOKEN" | jq
```

Liefert Subject, Aussteller, Seriennummer, SHA-256-Fingerabdruck, Schlüsselalgorithmus, Schlüssellänge, Verwendung, DNS-Namen, IP-Adressen und erweiterte Schlüsselverwendungen.

---

## 3. Zertifikate auf Zielsysteme einspielen

Jedes Zielprofil enthält profilspezifische Installationshinweise und ein Erneuerungsskript. Hier die wichtigsten Systeme:

### Proxmox VE

```bash
scp certificate.pem root@pve:/etc/pve/local/pveproxy-ssl.pem
scp key.pem root@pve:/etc/pve/local/pveproxy-ssl.key
ssh root@pve "systemctl restart pveproxy"
```

### nginx

```bash
cp fullchain.pem /etc/nginx/ssl/fullchain.pem
cp key.pem /etc/nginx/ssl/key.pem
nginx -t && systemctl reload nginx
```

nginx-Konfiguration:

```nginx
server {
    listen 443 ssl;
    server_name app.home.lab;
    ssl_certificate     /etc/nginx/ssl/fullchain.pem;
    ssl_certificate_key /etc/nginx/ssl/key.pem;
}
```

### HAProxy

HAProxy erwartet eine einzelne Datei mit Key, Zertifikat und Kette:

```bash
# Entweder den Bundle-Export direkt verwenden:
cp bundle.pem /etc/haproxy/certs/app.pem
systemctl reload haproxy

# Oder manuell zusammenfügen:
cat key.pem certificate.pem chain.pem > /etc/haproxy/certs/app.pem
systemctl reload haproxy
```

### Windows IIS

```powershell
# PFX importieren
$cert = Import-PfxCertificate -FilePath certificate.pfx -CertStoreLocation Cert:\LocalMachine\My

# IIS-Bindung zuweisen (PowerShell-Modul WebAdministration)
Import-Module WebAdministration
New-WebBinding -Name "Default Web Site" -Protocol https -Port 443 -HostHeader app.home.lab
$binding = Get-WebBinding -Name "Default Web Site" -Protocol https
$binding.AddSslCertificate($cert.Thumbprint, "My")
```

### Windows RDP

```powershell
$cert = Import-PfxCertificate -FilePath certificate.pfx -CertStoreLocation Cert:\LocalMachine\My
$path = (Get-WmiObject -Class Win32_TSGeneralSetting -Namespace root\cimv2\TerminalServices)
$path.SetSSLCertificateSHA1Hash($cert.Thumbprint)
```

### OPNsense

1. System > Trust > Certificates > Hinzufügen
2. Method: „Import an existing Certificate"
3. Zertifikat (PEM) und privaten Schlüssel einfügen
4. Speichern, dann unter System > Settings > Administration als Web-GUI-Zertifikat auswählen

### Home Assistant

Lege `certificate.pem` und `key.pem` im SSL-Verzeichnis ab und ergänze `configuration.yaml`:

```yaml
http:
  ssl_certificate: /ssl/certificate.pem
  ssl_key: /ssl/key.pem
```

```bash
ha core restart
```

### UniFi OS

Bei UniFi OS: Einstellungen > System > Erweitert > TLS-Zertifikat hochladen (`certificate.pem` und `key.pem`).

Bei selbst gehostetem Controller:

```bash
openssl pkcs12 -export -in certificate.pem -inkey key.pem \
  -out unifi.p12 -name unifi -password pass:aircontrolenterprise
keytool -importkeystore -srckeystore unifi.p12 -srcstoretype PKCS12 \
  -srcstorepass aircontrolenterprise -destkeystore /var/lib/unifi/keystore \
  -deststorepass aircontrolenterprise -destkeypass aircontrolenterprise -noprompt
systemctl restart unifi
```

### Synology DSM

DSM > Systemsteuerung > Sicherheit > Zertifikat > Hinzufügen > Vorhandenes Zertifikat importieren:
- Zertifikat: `certificate.pem`
- Privater Schlüssel: `key.pem`
- Zwischenzertifikat: `chain.pem`

### Cisco IOS/IOS-XE

```
conf t
no crypto pki certificate chain HOMECA
crypto pki import HOMECA pkcs12 terminal password <pfx-kennwort>
! Base64-kodierten PFX-Inhalt einfügen
ip http secure-trustpoint HOMECA
end
write memory
```

### TeamCity

Bei direktem HTTPS (Java-Keystore):

```bash
keytool -importkeystore -srckeystore certificate.pfx -srcstoretype PKCS12 \
  -destkeystore /opt/teamcity/conf/keystore.jks -deststoretype JKS \
  -deststorepass changeit -noprompt
systemctl restart teamcity
```

Bei Reverse Proxy: Verwende das PEM-Profil des Proxys (z. B. nginx oder HAProxy).

---

## 4. Automatische Erneuerung einrichten

### 4.1 Erneuerungsplan über die Weboberfläche

1. Navigiere zu **Erneuerungsautomatik**.
2. Wähle ein Zertifikat aus der Dropdown-Liste.
3. Setze die Anzahl Tage vor Ablauf, ab der erneuert werden soll (Standard: 30).
4. Aktiviere den Plan und speichere.

Der Hintergrunddienst prüft stündlich alle aktiven Pläne und stellt das Zertifikat automatisch mit denselben SANs, demselben Algorithmus und derselben Laufzeit neu aus. Der Plan wird anschließend auf das neue Zertifikat umgehängt.

### 4.2 Erneuerungsplan über die API

```bash
# Plan erstellen
curl -s http://127.0.0.1:5080/api/v1/renewal-plans \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "certificateId": "<zertifikat-id>",
    "renewBeforeDays": 30,
    "enabled": true
  }'

# Alle Pläne auflisten
curl -s http://127.0.0.1:5080/api/v1/renewal-plans \
  -H "Authorization: Bearer $TOKEN"

# Plan aktualisieren
curl -s -X PUT http://127.0.0.1:5080/api/v1/renewal-plans/<plan-id> \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"renewBeforeDays": 14, "enabled": true}'

# Plan löschen
curl -s -X DELETE http://127.0.0.1:5080/api/v1/renewal-plans/<plan-id> \
  -H "Authorization: Bearer $TOKEN"
```

### 4.3 Deployment-Paket nach Erneuerung

Jedes ausgestellte Zertifikat — ob manuell oder automatisch erneuert — erzeugt ein vollständiges Deployment-Paket im Exportverzeichnis (`exports/<id>/`):

| Datei | Inhalt |
|---|---|
| `certificate.pem` | Neues Serverzertifikat |
| `key.pem` | Neuer privater Schlüssel |
| `chain.pem` | CA-Kette |
| `fullchain.pem` | Zertifikat + Kette |
| `bundle.pem` | Key + Zertifikat + Kette |
| `profile-snapshot.json` | Profilstand zum Zeitpunkt der Ausstellung |
| `README.md` | Installationsanleitung |
| `install.ps1` | Profilspezifisches Erneuerungsskript |
| `checksums.json` | SHA-256-Prüfsummen aller Dateien |

Die Verteilung auf die Zielsysteme liegt beim Betreiber. Das `install.ps1`-Skript enthält profilspezifische Befehle als Vorlage — prüfe und passe es vor der Ausführung an.

Über **Paket ZIP** im Zertifikatsinventar lässt sich dieser vollständige Snapshot für ein ausgestelltes Zertifikat als einzelnes Archiv herunterladen.

---

## 5. Zertifikat sperren

### Über die Weboberfläche

Im Zertifikatsinventar auf **Widerrufen** klicken und mit einem zweiten Klick bestätigen. Das Zertifikat wird automatisch in die Sperrliste aufgenommen, die CRL wird neu generiert, und die Zertifikatsdateien werden gelöscht.

### Über die API

```bash
curl -s -X DELETE \
  "http://127.0.0.1:5080/api/v1/certificates/<id>?reason=keyCompromise" \
  -H "Authorization: Bearer $TOKEN"
```

Zulässige Sperrgründe: `unspecified`, `keyCompromise`, `cessationOfOperation`.

### CRL manuell erzeugen

Falls nötig, kannst du die CRL auch unabhängig von einer Sperrung neu generieren:

```bash
curl -s -X POST http://127.0.0.1:5080/api/v1/crl \
  -H "Authorization: Bearer $TOKEN"
```

Die aktuelle CRL ist jederzeit ohne Authentifizierung abrufbar:

```bash
curl -s -o homeca.crl http://127.0.0.1:5080/api/v1/crl/latest
openssl crl -in homeca.crl -inform DER -noout -text
```

---

## 6. Ablaufwarnungen überwachen

```bash
curl -s http://127.0.0.1:5080/api/v1/warnings/expiring \
  -H "Authorization: Bearer $TOKEN" | jq
```

Liefert alle Zertifikate, die innerhalb von 30 Tagen ablaufen. Integriere diesen Endpunkt in ein tägliches Monitoring (z. B. Cron-Job, Nagios-Check, Prometheus-Exporter).

Die Weboberfläche zeigt Ablaufwarnungen auf der Übersichtsseite als orangefarbene Meldung an.

---

## 7. Typischer Ablauf von Anfang bis Ende

Zusammenfassung für die erste Inbetriebnahme:

1. **HomeCA installieren und starten** — siehe [LXC-SETUP.md](LXC-SETUP.md)
2. **Administrator einrichten** — Setup-Endpunkt vom Loopback-Host
3. **CAs initialisieren** — Root-CA und Issuing-CA anlegen
4. **Root-CA verteilen** — siehe [TRUST-INSTALLATION.md](TRUST-INSTALLATION.md)
5. **Zertifikat ausstellen** — Profil wählen, DNS-Namen eingeben, ausstellen
6. **Exportdateien auf Zielsystem übertragen** — PEM, Key, Chain je nach Zielsystem
7. **Dienst neu starten** — nginx, HAProxy, Proxmox, IIS etc.
8. **Erneuerungsplan anlegen** — Automatische Neuausstellung vor Ablauf
9. **Monitoring einrichten** — Ablaufwarnungen regelmäßig abrufen
10. **Backup erstellen** — Verschlüsseltes Backup nach jeder Konfigurationsänderung, siehe [OPERATIONS.md](OPERATIONS.md)
