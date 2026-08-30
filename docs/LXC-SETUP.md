# HomeCA in einem Proxmox-Debian-LXC

Diese Anleitung richtet eine einzelne HomeCA-Instanz in einem Debian-12-LXC ein. HomeCA ist eine private CA: sichere den Container wie ein administratives Kernsystem und veröffentliche ihn nicht direkt im Internet.

## 1. Container in Proxmox anlegen

Erstelle im Proxmox-Webinterface einen **unprivilegierten** LXC mit dem Debian-12-Template.

| Einstellung | Empfehlung |
| --- | --- |
| CPU | 1 vCPU, bei vielen Ausstellungen 2 vCPU |
| Arbeitsspeicher | 1 GiB, mindestens 512 MiB |
| Systemdisk | mindestens 8 GiB, zusätzliches Backup-Ziel empfohlen |
| Netzwerk | feste DHCP-Reservierung oder statische interne Adresse |
| Features | keine verschachtelte Virtualisierung; `nesting` nur falls es anderweitig benötigt wird |

Gib dem Container keinen öffentlichen Port. Die mitgelieferte systemd-Unit bindet HomeCA absichtlich nur an `127.0.0.1:5080`. Für Zugriff aus dem LAN ist ein separater Reverse Proxy oder ein kontrollierter SSH-Tunnel erforderlich.

## 2. Grundsystem vorbereiten

Melde dich an der LXC-Konsole an und aktualisiere das System:

```bash
apt update
apt full-upgrade
apt install --yes ca-certificates curl openssh-client
```

Installiere anschließend das .NET-10-Runtime-Paket aus der offiziellen Microsoft-Paketquelle für Debian. Prüfe die Installation:

```bash
dotnet --info
```

Lege den Dienstbenutzer ohne Login-Shell an:

```bash
adduser --system --group --home /var/lib/homeca --shell /usr/sbin/nologin homeca
```

## 3. Anwendung und Schlüssel einspielen

Kopiere den veröffentlichten Service nach `/opt/homeca`; dort müssen mindestens `HomeCA.Service.dll`, die zugehörigen Runtime-Dateien und `profiles.json` liegen.

```bash
install -d -o root -g root -m 0755 /opt/homeca
# Release-Artefakte nach /opt/homeca kopieren
install -d -o homeca -g homeca -m 0750 /var/lib/homeca /var/backups/homeca /etc/homeca
umask 077
head -c 32 /dev/urandom > /etc/homeca/backup.key
chown homeca:homeca /etc/homeca/backup.key
chmod 0600 /etc/homeca/backup.key
```

Bewahre den Backup-Schlüssel getrennt vom LXC und von seinen Backups auf. Ohne ihn sind HCAB1-Backups nicht wiederherstellbar.

## 4. systemd-Dienst aktivieren

Kopiere `deploy/systemd/homeca.service` nach `/etc/systemd/system/homeca.service`, dann:

```bash
systemctl daemon-reload
systemctl enable --now homeca
systemctl status homeca
curl --fail http://127.0.0.1:5080/health
```

Logs sind über `journalctl -u homeca -f` verfügbar. Die Unit erlaubt Schreibzugriff ausschließlich auf die Daten- und Backupverzeichnisse.

## 5. Ersteinrichtung und Freigabe

1. Führe die lokale Administrator-Ersteinrichtung aus, solange der Setup-Endpunkt nur lokal erreichbar ist.
2. Initialisiere Root- und Issuing-CA.
3. Lege mindestens eine interne Ausstellungszone an und stelle ein Testzertifikat aus.
4. Prüfe Zertifikatskette, CRL, Backup und die DNS-Connector-Berechtigungen.
5. Richte erst danach den LAN-Zugriff über einen TLS-terminierenden Reverse Proxy mit eigener Zugriffskontrolle ein.

## 6. Aktualisierung und Notfallablauf

Vor einem Update ein verifiziertes Backup erzeugen. Dann Dienst anhalten, Release-Dateien in `/opt/homeca` austauschen, Dienst starten und Healthcheck sowie CA-Inventar prüfen:

```bash
systemctl stop homeca
# Release austauschen
systemctl start homeca
curl --fail http://127.0.0.1:5080/health
```

Für Wiederherstellung und regelmäßige Prüfungen siehe [OPERATIONS.md](OPERATIONS.md).
