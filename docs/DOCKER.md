# HomeCA im Container betreiben

Neben der LXC-Installation läuft HomeCA als Container. Das Image enthält .NET, den OpenSSH-Client für SSH-Zertifikate und ein Startskript, das beim ersten Start den CA- und den Backup-Schlüssel erzeugt.

## Starten

```bash
docker compose up -d --build
docker compose logs homeca
```

Danach `http://<host>:5080` öffnen und mit `admin` / `admin` anmelden; HomeCA verlangt sofort ein neues Kennwort. Setze in `docker-compose.yml` die Variable `Storage__PublicUrl` auf die Adresse, unter der Clients HomeCA erreichen – sie landet als CRL-Adresse in jedem Zertifikat.

## Volumes

| Pfad im Container | Inhalt |
| --- | --- |
| `/data` | CAs, Zertifikate, Exporte, Zustand |
| `/config` | `ca.key`, `backup.key` und Laufzeitkonfiguration |
| `/backups` | Verschlüsselte Backups |

**Sichere `/config` getrennt.** Ohne `ca.key` lassen sich die CA-Schlüssel nicht mehr öffnen, ohne `backup.key` keine Backups entschlüsseln. Für geplante Backups auf ein NAS eine Freigabe als zusätzliches Volume einbinden und dessen Pfad im Container als zweites Verzeichnis eintragen.

## TLS

Die TLS-Aktivierung in der Weboberfläche arbeitet mit systemd und funktioniert im Container nicht. Stelle einen Reverse Proxy (Caddy, Traefik, nginx) vor den Container, siehe [REVERSE-PROXY.md](REVERSE-PROXY.md). Port `5080` nicht ins Internet freigeben.

## Aktualisieren

```bash
git pull
docker compose up -d --build
```

Die Daten liegen in den Volumes und bleiben erhalten. Vor jedem Update ein Backup anlegen.
