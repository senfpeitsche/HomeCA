# HomeCA in einem Proxmox-Debian-LXC

Diese Anleitung richtet eine einzelne HomeCA-Instanz in einem Debian-12-LXC ein. HomeCA ist eine private CA: sichere den Container wie ein administratives Kernsystem und veröffentliche ihn nicht direkt im Internet.

## Schnellstart — One-Liner-Installation

Öffne die **Proxmox-Shell** (Knoten → Shell im Webinterface) und füge ein:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/senfpeitsche/HomeCA/main/deploy/scripts/homeca-lxc.sh)"
```

Das Script erstellt automatisch einen unprivilegierten Debian-12-LXC, installiert .NET 10, HomeCA und den systemd-Dienst. Danach ist HomeCA unter `http://127.0.0.1:5080` im Container erreichbar.

### Standardwerte anpassen

Alle Einstellungen lassen sich über Umgebungsvariablen überschreiben:

```bash
HOMECA_CTID=110 HOMECA_HOSTNAME=pki HOMECA_RAM=2048 \
  bash -c "$(curl -fsSL https://raw.githubusercontent.com/senfpeitsche/HomeCA/main/deploy/scripts/homeca-lxc.sh)"
```

| Variable | Standard | Beschreibung |
| --- | --- | --- |
| `HOMECA_CTID` | nächste freie ID | Container-ID |
| `HOMECA_HOSTNAME` | `homeca` | Hostname des LXC |
| `HOMECA_DISK` | `8` | Root-Disk in GiB |
| `HOMECA_RAM` | `1024` | Arbeitsspeicher in MiB |
| `HOMECA_CORES` | `1` | CPU-Kerne |
| `HOMECA_STORAGE` | `local-lvm` | Proxmox-Speicherpool |
| `HOMECA_BRIDGE` | `vmbr0` | Netzwerkbrücke |
| `HOMECA_NET` | `dhcp` | IP-Konfiguration (`dhcp` oder CIDR, z. B. `10.0.0.50/24,gw=10.0.0.1`) |
| `HOMECA_VERSION` | `latest` | Release-Tag (z. B. `v1.2.0`) |

### Nur den Container einrichten (ohne Proxmox-Host-Script)

Falls der LXC bereits existiert, kann das Install-Script direkt im Container ausgeführt werden:

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/senfpeitsche/HomeCA/main/deploy/scripts/homeca-install.sh)
```

## Update

### Direkt im Container:

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/senfpeitsche/HomeCA/main/deploy/scripts/homeca-update.sh)
```

### Vom Proxmox-Host aus (Container-ID anpassen):

```bash
curl -fsSL https://raw.githubusercontent.com/senfpeitsche/HomeCA/main/deploy/scripts/homeca-update.sh \
  -o /tmp/homeca-update.sh
pct push 100 /tmp/homeca-update.sh /tmp/homeca-update.sh
pct exec 100 -- bash /tmp/homeca-update.sh
```

### Bestimmte Version installieren:

```bash
HOMECA_VERSION=v1.3.0 bash <(curl -fsSL https://raw.githubusercontent.com/senfpeitsche/HomeCA/main/deploy/scripts/homeca-update.sh)
```

### TLS wieder deaktivieren (zurück zu HTTP)

Im Container als `root` ausführen:

```bash
bash /opt/homeca/homeca-deactivate-tls.sh
```

Das Skript entfernt nur den aktiven systemd-TLS-Override, prüft anschließend
`http://127.0.0.1:5080/health` und sichert den Override unter
`/etc/homeca/tls.conf.disabled`. Zertifikat und TLS-Konfiguration bleiben für
eine spätere Reaktivierung erhalten.

### Was das Update-Script macht:

1. Prüft, ob die Zielversion bereits installiert ist
2. Aktualisiert Systempakete
3. Erstellt ein Backup (Daten + Konfiguration) unter `/var/backups/homeca/`
4. Stoppt den Dienst und sichert die vorherige Version als Rollback
5. Lädt das neue Release herunter und deployt es
6. Aktualisiert die systemd-Unit, falls sie sich im Repository geändert hat
7. Startet den Dienst und prüft den Health-Endpoint
8. Rollt bei fehlgeschlagenem Healthcheck automatisch zurück

## Ersteinrichtung nach Installation

1. Führe die Administrator-Ersteinrichtung über den lokalen Endpoint aus (`http://127.0.0.1:5080`)
2. Initialisiere Root- und Issuing-CA
3. Lege mindestens eine interne Ausstellungszone an und stelle ein Testzertifikat aus
4. Prüfe Zertifikatskette, CRL, Backup und die DNS-Connector-Berechtigungen
5. Richte erst danach den LAN-Zugriff über einen TLS-terminierenden Reverse Proxy mit eigener Zugriffskontrolle ein

## Backup-Schlüssel

Bei der Installation wird automatisch ein AES-256-Verschlüsselungsschlüssel unter `/etc/homeca/backup.key` erzeugt. **Bewahre eine Kopie dieses Schlüssels außerhalb des Containers auf.** Ohne ihn sind HCAB1-Backups nicht wiederherstellbar.

```bash
# Schlüssel aus dem Container kopieren (auf dem Proxmox-Host)
pct pull 100 /etc/homeca/backup.key ./homeca-backup.key
```

## Manuelle Installation (Referenz)

Falls die automatischen Scripts nicht verwendet werden sollen, dokumentiert dieser Abschnitt die einzelnen Schritte.

### 1. Container in Proxmox anlegen

Erstelle im Proxmox-Webinterface einen **unprivilegierten** LXC mit dem Debian-12-Template.

| Einstellung | Empfehlung |
| --- | --- |
| CPU | 1 vCPU, bei vielen Ausstellungen 2 vCPU |
| Arbeitsspeicher | 1 GiB, mindestens 512 MiB |
| Systemdisk | mindestens 8 GiB, zusätzliches Backup-Ziel empfohlen |
| Netzwerk | feste DHCP-Reservierung oder statische interne Adresse |
| Features | keine verschachtelte Virtualisierung; `nesting` nur falls es anderweitig benötigt wird |

Gib dem Container keinen öffentlichen Port. Die mitgelieferte systemd-Unit bindet HomeCA absichtlich nur an `127.0.0.1:5080`. Für Zugriff aus dem LAN ist ein separater Reverse Proxy oder ein kontrollierter SSH-Tunnel erforderlich.

### 2. Grundsystem vorbereiten

```bash
apt update
apt full-upgrade
apt install --yes ca-certificates curl openssh-client
```

Installiere das .NET-10-Runtime-Paket aus der Microsoft-Paketquelle für Debian. Prüfe mit `dotnet --info`.

Lege den Dienstbenutzer an:

```bash
adduser --system --group --home /var/lib/homeca --shell /usr/sbin/nologin homeca
```

### 3. Anwendung und Schlüssel einspielen

```bash
install -d -o root -g root -m 0755 /opt/homeca
# Release-Artefakte nach /opt/homeca kopieren
install -d -o homeca -g homeca -m 0750 /var/lib/homeca /var/backups/homeca /etc/homeca
umask 077
head -c 32 /dev/urandom > /etc/homeca/backup.key
chown homeca:homeca /etc/homeca/backup.key
chmod 0600 /etc/homeca/backup.key
```

### 4. systemd-Dienst aktivieren

```bash
cp deploy/systemd/homeca.service /etc/systemd/system/homeca.service
systemctl daemon-reload
systemctl enable --now homeca
curl --fail http://127.0.0.1:5080/health
```

### 5. Manuelles Update

```bash
systemctl stop homeca
# Release in /opt/homeca austauschen
systemctl start homeca
curl --fail http://127.0.0.1:5080/health
```

Für Wiederherstellung und regelmäßige Prüfungen siehe [OPERATIONS.md](OPERATIONS.md).

## Script-Übersicht

| Script | Zweck | Ausführungsort |
| --- | --- | --- |
| `deploy/scripts/homeca-lxc.sh` | LXC erstellen + Installation anstoßen | Proxmox-Shell |
| `deploy/scripts/homeca-install.sh` | HomeCA im Container installieren | Im LXC (root) |
| `deploy/scripts/homeca-update.sh` | HomeCA aktualisieren mit Backup + Rollback | Im LXC (root) oder via `pct push`/`pct exec` |
