# HomeCA in einem Proxmox-Debian-LXC

Diese Anleitung richtet eine einzelne HomeCA-Instanz in einem Debian-13-LXC ein. HomeCA ist eine private CA: sichere den Container wie ein administratives Kernsystem und veröffentliche ihn nicht direkt im Internet.

## Schnellstart — versionierte Installation

Öffne die **Proxmox-Shell** (Knoten → Shell im Webinterface), wähle einen
Release-Tag und führe dessen Bootstrap aus:

```bash
export HOMECA_VERSION=v1.3.0
curl -fsSLO "https://github.com/senfpeitsche/HomeCA/releases/download/${HOMECA_VERSION}/homeca-lxc.sh"
bash ./homeca-lxc.sh
```

Das Script nutzt die Engine der [Proxmox VE Helper-Scripts](https://community-scripts.org/) (`community-scripts/core`, auf einen Commit festgelegt). Sie fragt per Dialog nach Standard- oder erweiterten Einstellungen: Storage für Container und Template, aktuelles Debian-13-Template, Netzwerk (DHCP oder statisch, VLAN, MTU), SSH-Schlüssel und Ressourcen. Danach installiert sie im unprivilegierten Container .NET 10, HomeCA mit dem Dienstbenutzer `homeca` und den systemd-Dienst, alles aus dem gewählten Release mit SHA256-Prüfung. HomeCA ist dann im LAN auf Port `5080` erreichbar. Dieser HTTP-Zugang ist nur für die Ersteinrichtung gedacht: aktiviere danach TLS und begrenze den Zugriff auf Admin- und Servernetze.

Die Engine kann anonyme Diagnosedaten an community-scripts senden und fragt beim ersten Lauf danach.

### Standardwerte anpassen

Standardwerte lassen sich über Umgebungsvariablen vorgeben, die Dialoge übernehmen sie:

```bash
HOMECA_CTID=110 HOMECA_HOSTNAME=pki HOMECA_RAM=2048 bash ./homeca-lxc.sh
```

| Variable | Standard | Beschreibung |
| --- | --- | --- |
| `HOMECA_CTID` | nächste freie ID | Container-ID (`var_ctid`) |
| `HOMECA_HOSTNAME` | `homeca` | Hostname des LXC (`var_hostname`) |
| `HOMECA_DISK` | `8` | Root-Disk in GiB (`var_disk`) |
| `HOMECA_RAM` | `1024` | Arbeitsspeicher in MiB (`var_ram`) |
| `HOMECA_CORES` | `1` | CPU-Kerne (`var_cpu`) |
| `HOMECA_STORAGE` | Abfrage | Storage für die Container-Disk (`var_container_storage`) |
| `HOMECA_BRIDGE` | `vmbr0` | Netzwerkbrücke (`var_brg`) |
| `HOMECA_NET` | `dhcp` | `dhcp` oder CIDR, z. B. `10.0.0.50/24,gw=10.0.0.1` (`var_net`, `var_gateway`) |
| `HOMECA_VERSION` | Release des Scripts | Release-Tag (z. B. `v1.2.0`) |

Alle weiteren Einstellungen der Engine funktionieren ebenfalls als `var_*`-Variable, etwa `var_template_storage`, `var_vlan` oder `var_ssh=yes`.

Ein bestehender Debian-12-Container bleibt auf Debian 12; `homeca-update.sh` funktioniert dort weiter.

### Nur den Container einrichten (ohne Proxmox-Host-Script)

Falls der LXC bereits existiert, kann das Install-Script direkt im Container ausgeführt werden:

```bash
export HOMECA_VERSION=v1.3.0
curl -fsSLO "https://github.com/senfpeitsche/HomeCA/releases/download/${HOMECA_VERSION}/homeca-install.sh"
bash ./homeca-install.sh
```

## Update

### Direkt im Container:

```bash
/opt/homeca/homeca-update.sh
```

### Vom Proxmox-Host aus (Container-ID anpassen):

```bash
pct exec 100 -- /opt/homeca/homeca-update.sh
```

### Bestimmte Version installieren:

```bash
HOMECA_VERSION=v1.3.0 /opt/homeca/homeca-update.sh
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

### TLS über die Weboberfläche aktivieren

Öffne **Einstellungen > Webserver TLS**, gib den DNS-Namen ein und stelle das Zertifikat aus. Mit **TLS jetzt aktivieren und neu starten** wird der systemd-Override geschrieben und HomeCA auf HTTPS umgestellt. Der Browser wechselt nach dem Neustart automatisch zur konfigurierten Zieladresse (üblicherweise `https://<hostname>:5443`).

Installiere vorher die Root-CA auf dem verwendeten Browser oder Betriebssystem; siehe [TRUST-INSTALLATION.md](TRUST-INSTALLATION.md). Falls der Wechsel fehlschlägt, im Container `bash /opt/homeca/homeca-deactivate-tls.sh` ausführen, um sicher zu HTTP zurückzukehren.

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

1. Führe die Administrator-Ersteinrichtung über die HomeCA-LAN-Adresse aus (`http://<homeca-host>:5080`) und nur aus einem vertrauenswürdigen Admin-Netz.
2. Initialisiere Root- und Issuing-CA
3. Lege mindestens eine interne Ausstellungszone an und stelle ein Testzertifikat aus
4. Prüfe Zertifikatskette, CRL, Backup und die DNS-Connector-Berechtigungen
5. Aktiviere TLS und begrenze den LAN-Zugriff mit Host-Firewall oder einem TLS-terminierenden Reverse Proxy. Eine sofort nutzbare Referenz steht unter [REVERSE-PROXY.md](REVERSE-PROXY.md).

## Backup-Schlüssel

Bei der Installation werden zwei getrennte 32-Byte-Schlüssel erzeugt: `/etc/homeca/backup.key` für Backups und `/etc/homeca/ca.key` zum Schutz der CA-PFX-Dateien. **Bewahre eine Kopie beider Schlüssel außerhalb des Containers auf.** Ohne den Backup-Schlüssel sind HCAB1-Backups nicht wiederherstellbar; ohne den CA-Schlüssel können die CA-Private-Keys nicht geladen werden.

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

Gib dem Container keinen öffentlichen Port. Die mitgelieferte systemd-Unit stellt HomeCA im LAN auf Port `5080` bereit. Für den dauerhaften Betrieb TLS aktivieren und den Zugriff mit Host-Firewall oder Reverse Proxy auf Admin- und Servernetze begrenzen.

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
head -c 32 /dev/urandom > /etc/homeca/ca.key
chown homeca:homeca /etc/homeca/ca.key
chmod 0600 /etc/homeca/ca.key
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
| `deploy/proxmox/ct/homeca.sh` (Release: `homeca-lxc.sh`) | LXC über community-scripts/core erstellen + Installation anstoßen | Proxmox-Shell |
| `deploy/proxmox/install/homeca-install.sh` | Container vorbereiten und `homeca-install.sh` aus dem Release starten | Im LXC, von der Engine aufgerufen |
| `deploy/scripts/homeca-install.sh` | HomeCA im Container installieren | Im LXC (root) |
| `deploy/scripts/homeca-update.sh` | HomeCA aktualisieren mit Backup + Rollback | Im LXC (root) oder via `pct push`/`pct exec` |
| `deploy/scripts/homeca-activate-tls.sh` | TLS-Listener aus der vorbereiteten TLS-Konfiguration aktivieren | Im LXC (root), normalerweise über die Weboberfläche ausgelöst |
| `deploy/scripts/homeca-deactivate-tls.sh` | TLS-Override entfernen und HTTP wiederherstellen | Im LXC (root) |
