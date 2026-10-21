# Root-CA-Vertrauensstellung einrichten

Damit Geräte und Browser den von HomeCA ausgestellten Zertifikaten vertrauen, muss das Root-CA-Zertifikat im jeweiligen Vertrauensspeicher installiert werden.

Installiere **nur die Root-CA** als Vertrauensanker. Das Intermediate-/Issuing-Zertifikat wird zusammen mit Serverzertifikaten ausgeliefert (etwa über `chain.pem`, `fullchain.pem` oder den PFX-Export) und gehört nicht in den Truststore von Clients.

## Trust-Seite für Geräte

`/trust` ist eine öffentliche Seite, die ohne Anmeldung und über einfaches HTTP funktioniert. So kann ein Gerät, das HomeCA noch nicht vertraut, sie ohne Warnung öffnen. Sie erkennt das Betriebssystem, öffnet die passende Anleitung, bietet ein Apple-Konfigurationsprofil (`GET /api/v1/trust-anchor/mobileconfig`) für iPhone, iPad und Mac, zeigt SHA-256- und SHA-1-Fingerprint sowie einen QR-Code für Handys. Übersicht und Anmeldeseite verlinken sie.

## Begrenzen, wofür die Root bürgt

Beim Anlegen oder Rotieren einer Intermediate-CA fügt **Auf eigene Namen beschränken** X.509-Name-Constraints mit deinen DNS-Domains und privaten Netzen hinzu. Clients lehnen dann Zertifikate dieser CA für alle anderen Namen ab, und HomeCA stellt solche gar nicht erst aus. Das macht die Root-Installation auf Alltagsgeräten deutlich weniger riskant. Bestehende Intermediates behalten ihre Eigenschaften; um die Beschränkung zu nutzen, auf eine beschränkte Intermediate rotieren.

## Root-CA-Zertifikat herunterladen

HomeCA stellt das Root-CA-Zertifikat ohne Authentifizierung bereit. Ersetze `HOMECA_BASE_URL` durch die erreichbare Basis-URL, zum Beispiel `http://homeca:5080` oder nach TLS-Aktivierung `https://homeca.example.lan:5443`.

```
# Beispiele: http://homeca:5080 oder https://homeca.example.lan:5443
HOMECA_BASE_URL="https://homeca.example.lan:5443"

# PEM-Format (Linux, macOS, Firefox, Appliances)
curl -o homeca-root-ca.pem "$HOMECA_BASE_URL/api/v1/trust-anchor/pem"

# DER-Format (Windows .cer)
curl -o homeca-root-ca.cer "$HOMECA_BASE_URL/api/v1/trust-anchor/der"

# Metadaten und SHA-256-Fingerprint zur Verifizierung
curl "$HOMECA_BASE_URL/api/v1/trust-anchor"
```

Prüfe den SHA-256-Fingerprint immer gegen die Ausgabe des `/api/v1/trust-anchor`-Endpunkts, bevor du das Zertifikat installierst.

---

## Windows

### Einzelner Rechner (manuell)

```powershell
# Als Administrator ausführen
certutil -addstore -f "Root" homeca-root-ca.cer
```

Alternativ: Doppelklick auf die `.cer`-Datei, „Zertifikat installieren", Speicherort „Lokaler Computer", Speicher „Vertrauenswürdige Stammzertifizierungsstellen".

### Per Gruppenrichtlinie (Active Directory)

1. `gpedit.msc` oder Gruppenrichtlinien-Management öffnen
2. Computerkonfiguration > Windows-Einstellungen > Sicherheitseinstellungen > Richtlinien öffentlicher Schlüssel > Vertrauenswürdige Stammzertifizierungsstellen
3. Rechtsklick > Importieren > `.cer`-Datei auswählen

### RDP-Clients

Windows-Rechner, die das Root-CA per `certutil` oder GPO installiert haben, akzeptieren automatisch RDP-Zertifikate, die von HomeCA signiert sind. Auf dem RDP-Zielserver muss das TLS-Zertifikat in den Zertifikatsspeicher importiert und der RDP-Dienst konfiguriert werden:

```powershell
# Zertifikat importieren
$cert = Import-PfxCertificate -FilePath certificate.pfx -CertStoreLocation Cert:\LocalMachine\My
# RDP-Bindung setzen
$path = (Get-WmiObject -Class Win32_TSGeneralSetting -Namespace root\cimv2\TerminalServices).SetSSLCertificateSHA1Hash($cert.Thumbprint)
```

---

## Linux (Debian/Ubuntu)

```bash
sudo cp homeca-root-ca.pem /usr/local/share/ca-certificates/homeca-root-ca.crt
sudo update-ca-certificates
```

Hinweis: Die Datei unter `/usr/local/share/ca-certificates/` muss die Endung `.crt` haben, auch wenn es PEM-Format ist.

### Proxmox VE Host

```bash
# Auf jedem Proxmox-Node ausführen
scp homeca-root-ca.pem root@pve-node:/usr/local/share/ca-certificates/homeca-root-ca.crt
ssh root@pve-node "update-ca-certificates"
```

---

## macOS

```bash
sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain homeca-root-ca.pem
```

Alternativ: Doppelklick auf die `.pem`-Datei, Schlüsselbundverwaltung öffnet sich, Zertifikat unter „System" ablegen, dann Doppelklick > Vertrauen > „Immer vertrauen".

---

## Firefox

Firefox verwendet einen eigenen Zertifikatsspeicher und ignoriert den Systemspeicher.

### Manuell

1. Einstellungen > Datenschutz & Sicherheit > Zertifikate > Zertifikate anzeigen
2. Reiter „Zertifizierungsstellen" > Importieren
3. `.pem`-Datei auswählen, „Dieser CA vertrauen, um Websites zu identifizieren" aktivieren

### Per Richtlinie (enterprise)

`policies.json` im Firefox-Installationsverzeichnis:

```json
{
  "policies": {
    "Certificates": {
      "ImportEnterpriseRoots": true,
      "Install": ["http://HOMECA:5080/api/v1/trust-anchor/pem"]
    }
  }
}
```

---

## OPNsense

1. System > Trust > Authorities > Hinzufügen
2. Method: „Import an existing Certificate Authority"
3. PEM-Inhalt einfügen
4. Speichern

---

## UniFi OS / UniFi Network

Die UniFi-Controller-Appliance nutzt den Java-Keystore. Auf der Appliance:

```bash
# Root-CA in den System-Truststore aufnehmen
cp homeca-root-ca.pem /usr/local/share/ca-certificates/homeca-root-ca.crt
update-ca-certificates
```

---

## Netzwerk-Switches (Cisco, Huawei)

### Cisco IOS/IOS-XE

```
conf t
crypto pki trustpoint HOMECA
 enrollment terminal
 revocation-check none
exit
crypto pki authenticate HOMECA
! PEM-Inhalt einfügen und mit quit bestätigen
```

### Huawei VRP

```
system-view
pki realm homeca
 ca-certificate import file homeca-root-ca.pem
```

Die genauen Befehle variieren je nach Firmware-Version. Prüfe die Dokumentation deines Switch-Modells.

---

## Home Assistant

Home Assistant selbst benötigt keine Root-CA-Installation — es nutzt das Zertifikat nur als TLS-Server. Die Root-CA muss auf den Clients installiert werden (Browser, App), die auf das Home-Assistant-Frontend zugreifen.

---

## Android

1. Einstellungen > Sicherheit > Verschlüsselung und Anmeldedaten > Zertifikate installieren > CA-Zertifikat
2. `.cer`-Datei auswählen

Hinweis: Ab Android 7 vertrauen Apps standardmäßig nur System-CAs. Benutzerdefinierte CAs gelten nur für den Browser, es sei denn die App konfiguriert `network-security-config` explizit.

## iOS / iPadOS

1. `.cer`-Datei per AirDrop, Mail oder URL öffnen
2. Einstellungen > Profil heruntergeladen > Installieren
3. Einstellungen > Allgemein > Info > Zertifikatsvertrauenseinstellungen > Root-Zertifikat aktivieren

---

## Verifizierung

Nach der Installation auf einem Client prüfen, ob das Zertifikat korrekt installiert ist:

```bash
# Linux: OpenSSL gegen einen HomeCA-gesicherten Dienst testen
openssl s_client -connect dienst.int.zikke.org:443 -CApath /etc/ssl/certs

# Windows: PowerShell
Test-NetConnection dienst.int.zikke.org -Port 443
# Oder im Browser: https://dienst.int.zikke.org sollte ohne Warnung laden

# Fingerprint prüfen
curl -s http://HOMECA:5080/api/v1/trust-anchor | python3 -m json.tool
```
