# Zertifikate automatisch verteilen

Ein **Verteilungsziel** folgt einem Zertifikat über alle Erneuerungen hinweg. Stellt HomeCA einen Nachfolger aus – automatisch über einen Erneuerungsplan oder über **Jetzt erneuern** –, zeigt das Ziel sofort auf das neue Zertifikat. Ein kleiner Agent auf dem Gerät holt die Dateien ab und führt das Profilskript aus.

## Einrichtung per SSH

Unter **Verteilung > Per SSH einrichten** übernimmt HomeCA die Einrichtung auf Linux-Geräten selbst:

1. Host, Port, Benutzer und Passwort oder privaten Schlüssel eingeben. Ein Benutzer ohne root braucht `sudo`; das sudo-Passwort ist nur nötig, wenn es vom SSH-Passwort abweicht.
2. Beim ersten Kontakt zeigt HomeCA den SHA-256-Fingerprint des Host-Keys und meldet sich erst nach deiner Bestätigung an. Vergleiche ihn an der Konsole des Geräts mit `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`.
3. HomeCA liest das System nur aus: Hostname, FQDN, Adressen, Rechte, systemd oder cron, Vertrauensspeicher und laufende Dienste (z. B. Proxmox, nginx, Caddy, Traefik, Jellyfin). Daraus schlägt es Profil, Namen und ein passendes vorhandenes Zertifikat vor; fehlt eines, lässt es sich direkt im Assistenten ausstellen.
4. **Agent installieren** legt das Ziel an (oder erneuert beim erneuten Einrichten das Token), schreibt Token, Agent und einen stündlichen systemd-Timer bzw. cron-Eintrag, nimmt auf Wunsch die Root-CA in den Systemspeicher auf und führt den Agenten einmal aus. Ausgabe und Exit-Code erscheinen direkt in der Oberfläche.

Passwort, Schlüssel, Passphrase und Token werden nie gespeichert oder protokolliert. Am Ziel bleiben nur Host, Port, Benutzername und der bestätigte Host-Key. Weicht der Host-Key später ab, bricht HomeCA vor jeder Anmeldung ab; nach einer Neuinstallation des Geräts übernimmst du den neuen Key ausdrücklich.

Erreicht das Gerät HomeCA nicht unter der Adresse im Browser (z. B. `localhost`), trage unter **HomeCA-URL aus Sicht des Geräts** die richtige Adresse ein. Vertraut das System dem TLS-Zertifikat von HomeCA nicht, nutzt der Agent automatisch eine Kopie der Root-CA (`CA_FILE` in `/etc/homeca-agent/<id>.env`).

## Windows

Für Windows gibt es einen PowerShell-Agenten (`agent.ps1`, Windows PowerShell 5.1 oder PowerShell 7). Er läuft stündlich als geplante Aufgabe `\HomeCA\Agent <id>` unter SYSTEM, holt die Dateien wie der Shell-Agent und führt `install.ps1` aus. Zusätzlich erhält er `certificate.pfx` mit Zertifikat, Schlüssel und ausstellender CA; das PFX ist mit dem Ziel-Token geschützt, und `install.ps1` bekommt das Kennwort über `HOMECA_PFX_PASSWORD`.

- **Einrichtung per SSH:** Ist auf dem Server OpenSSH aktiv (ab Windows 10 1809 und Server 2019 eingebaut), erkennt **Per SSH einrichten** Windows automatisch, auch mit cmd oder PowerShell als Standard-Shell und mit Git-Bash im PATH. Der Benutzer muss Administrator sein. HomeCA legt `%ProgramData%\HomeCA\Agent` mit Rechten nur für SYSTEM und Administratoren an, schreibt Token und Agent, nimmt auf Wunsch die Root-CA in `LocalMachine\Root` auf, registriert die Aufgabe und führt den Agenten einmal aus. Push gibt es für Windows nicht.
- **Ohne OpenSSH:** Nach dem Anlegen eines Ziels zeigt HomeCA unter **Windows** PowerShell-Befehle mit Token. Führe sie in einer PowerShell mit Administratorrechten aus, lokal oder in einer eigenen Remoting-Sitzung (`Enter-PSSession`, also auch über WinRM).
- **Vertrauen:** Vertraut Windows dem TLS-Zertifikat von HomeCA nicht, prüft der Agent die Kette gegen seine Kopie der Root-CA (`caFile` in `%ProgramData%\HomeCA\Agent\<id>.settings.json`).
- **Profile:** `iis` importiert das Zertifikat in `LocalMachine\My` und bindet alle HTTPS-Bindungen um, deren Zertifikat denselben Antragsteller hat. Eine neue Website bindest du einmal im IIS-Manager. `windows-certstore` importiert nur, etwa für RDP oder einen WinRM-HTTPS-Listener.
- **Backups:** Vor jeder Änderung sichert HomeCA unter `%ProgramData%\HomeCA\Backups\<Ziel>\<Zeitstempel>-<Anlass>\` die betroffenen Dateien, die geplante Aufgabe (`task.xml`), neu importierte Zertifikate und die vorherige Zuordnung jeder IIS-Bindung. `restore.ps1` in einer PowerShell mit Administratorrechten stellt den vorherigen Zustand her, das neueste Backup zuerst. Eigene Profile nutzen `Backup-HomeCAFile <Pfad>`, `Add-HomeCARestoreStep '<Befehl>'` und `Import-HomeCACertificate certificate.pfx`.

Eine direkte Einrichtung über WinRM bietet HomeCA nicht an: Von Linux aus gibt es dafür keinen gepflegten Client, und ohne Kerberos oder einen HTTPS-Listener fehlt eine Server-Authentisierung wie der SSH-Host-Key.

## Sofort verteilen per Push

Mit **Erneuerungen sofort verteilen** (Standard) trägt die Einrichtung einen eigenen HomeCA-Schlüssel (ECDSA P-256) in `authorized_keys` von root ein:

```
command="/usr/local/bin/homeca-agent-<id>",no-pty,no-port-forwarding,no-agent-forwarding,no-X11-forwarding ecdsa-sha2-nistp256 AAAA… homeca-push-<id>
```

Der Schlüssel kann nur den Agenten dieses Ziels starten: keine Shell, kein Terminal, keine Weiterleitung, egal welchen Befehl der Client sendet. Nach jeder Erneuerung meldet sich HomeCA mit dem gespeicherten Host-Key und diesem Schlüssel als root an und startet den Agenten; das Gerät hat das neue Zertifikat damit nach Sekunden statt nach bis zu einer Stunde. **Jetzt verteilen** in der Zielliste macht dasselbe von Hand.

- Direkt nach der Einrichtung prüft ein Test-Push, ob `sshd` den Schlüssel annimmt. Erlaubt `sshd` keine root-Anmeldung (`PermitRootLogin no`), bleibt Push aus.
- Läuft gerade schon ein Agent (z. B. der Timer), meldet der Agent Exit-Code 75; HomeCA versucht es bis zu viermal im Abstand von 30 Sekunden erneut.
- Schlägt ein Push fehl, gibt es die Benachrichtigung **Push fehlgeschlagen**. Der stündliche Timer bleibt immer aktiv und holt das Zertifikat trotzdem ab.
- Weicht der Host-Key ab, meldet sich HomeCA nicht an und der Push gilt als fehlgeschlagen.
- **Push abschalten** beendet die Pushes in HomeCA. Die Zeile auf dem Gerät entfernt `restore.sh` der Einrichtung oder eine erneute Einrichtung ohne Push. Andere Einträge in `authorized_keys` bleiben dabei unangetastet.
- Der private Schlüssel liegt verschlüsselt mit dem CA-Schlüssel unter `state/ssh-push-key.pem` und ist Teil des HomeCA-Backups.

## Backups auf dem Gerät

Vor jeder Änderung sichert HomeCA die betroffenen Dateien unter `/var/backups/homeca/<Ziel>/<Zeitstempel>-<Anlass>/`:

- `-setup`: alles, was die Einrichtung schreibt (Token, Agent, Timer, cron, Root-CA).
- `-renewal`: alles, was das Profilskript beim Agentenlauf ersetzt, etwa `/etc/nginx/ssl/fullchain.pem`.

Jedes Backup enthält `files.txt` und `restore.sh`. `restore.sh` spielt überschriebene Dateien zurück und entfernt Dateien, die es vorher nicht gab. Mehrere Änderungen nimmst du in umgekehrter Reihenfolge zurück, das neueste Backup zuerst. Der Agent behält je Ziel die zehn neuesten Backups (`KEEP_BACKUPS`).

In Profilskripten sichern `cp` und `install` ihr Ziel automatisch. Schreibt ein eigenes Profil auf anderem Weg, etwa mit `>` oder `openssl -out`, rufe vorher `homeca_backup /pfad/zur/datei` auf. Ohne Agent landen die Backups unter `/var/backups/homeca/manual/`.

## Ablauf

1. Zertifikat ausstellen (Erneuerungsplan ist standardmäßig aktiv).
2. Unter **Verteilung** ein Ziel anlegen: Name, Zertifikat, optional Host und Port für die Prüfung sowie ein Webhook.
3. HomeCA zeigt **einmalig** ein Token und die Einrichtungsbefehle. Diese als root auf dem Gerät ausführen.
4. Der Agent läuft stündlich per cron. Er lädt das Manifest, vergleicht den Fingerprint mit dem installierten Zertifikat und handelt nur bei einer Änderung.

## Was der Agent tut

- `GET /api/v1/deploy/{id}/manifest` liefert Zertifikat-ID, SHA-256-Fingerprint, Ablaufdatum und die verfügbaren Dateien.
- `GET /api/v1/deploy/{id}/files/{datei}` liefert `certificate.pem`, `key.pem`, `chain.pem`, `fullchain.pem`, `bundle.pem` und das Profilskript (`install.sh`, `install.ps1` oder `device-commands.txt`).
- Alle Abrufe authentifizieren sich mit `Authorization: Bearer <Ziel-Token>`. Das Token gilt nur für dieses eine Ziel und nicht für die Verwaltungs-API.
- Die Dateien landen erst in einem temporären Verzeichnis. Ist `openssl` vorhanden, prüft der Agent den Fingerprint. Danach wird das Verzeichnis atomar nach `/var/lib/homeca-agent/<id>/current` verschoben; der vorherige Stand bleibt unter `previous` erhalten.
- Liegt `install.sh` vor, führt der Agent es im neuen Verzeichnis aus. Mit `RUN_SCRIPT=0` lädt er nur die Dateien.
- Liegt die HomeCA-Root-CA nicht im Systemspeicher, setzt `CA_FILE` den Pfad zur Root-CA.

`--force` installiert das aktuelle Zertifikat auch ohne Änderung erneut.

## Webhook

Ist ein Webhook hinterlegt, sendet HomeCA nach jeder Erneuerung einen JSON-POST:

```json
{ "event": "certificate.renewed", "targetId": "…", "targetName": "pve01", "certificateId": "…", "fingerprint": "…", "manifestUrl": "https://homeca…/api/v1/deploy/…/manifest" }
```

Die Nutzlast enthält keine Geheimnisse; der Empfänger holt die Dateien wie der Agent mit seinem Token ab. Ein nicht erreichbarer Webhook verhindert die Erneuerung nicht.

## Prüfung der ausgelieferten Zertifikate

Mit Host und Port verbindet sich HomeCA per TLS mit dem Gerät und vergleicht das ausgelieferte Zertifikat:

| Status | Bedeutung |
| --- | --- |
| Aktuell | Das Gerät liefert das aktuelle Zertifikat des Ziels aus. |
| Veraltetes Zertifikat | Das Gerät liefert ein älteres HomeCA-Zertifikat aus – meist wurde der Agent nicht ausgeführt oder der Dienst nicht neu geladen. |
| Fremdes Zertifikat | Das Gerät liefert ein Zertifikat, das HomeCA nicht kennt. |
| Nicht erreichbar | Keine TLS-Verbindung möglich. |

Die Prüfung läuft auf Knopfdruck und alle sechs Stunden im Hintergrund. Wechselt ein Ziel in einen anderen Zustand als „Aktuell“, gibt es eine Benachrichtigung, und die Übersicht zeigt es unter **Braucht Aufmerksamkeit**.

## Token verwalten

- **Neues Token** macht das alte sofort ungültig. Danach die Einrichtungsbefehle auf dem Gerät erneut ausführen oder nur die Token-Datei ersetzen.
- Token-Dateien liegen mit Rechten `600` unter `/etc/homeca-agent/<id>.token`.
- Wer ein Token besitzt, kann den privaten Schlüssel des Ziels abrufen. Behandle es wie den Schlüssel selbst.

## Geräte ohne Shell

Für Geräte mit Weboberfläche oder eigener CLI (FRITZ!Box, OPNsense, Switches) enthält das Paket `device-commands.txt` mit den Schritten. Für Geräte, die ihren Schlüssel selbst erzeugen, ist **CSR signieren** der bessere Weg; diese Zertifikate kann HomeCA nicht selbst erneuern.
