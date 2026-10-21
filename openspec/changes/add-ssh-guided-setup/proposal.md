# Geführte Einrichtung per SSH

## Warum

Ein Verteilungsziel einzurichten heißt heute: Befehle mit Token kopieren, sich selbst per SSH anmelden, als root einfügen. Das ist fehleranfällig, das Token landet in Shell-History und Zwischenablage, und niemand weiß vorher, welches Profil und welche Namen zum Gerät passen. Außerdem überschreiben Profilskripte bestehende Zertifikatsdateien ohne Sicherung.

## Was ändert sich

- **Einrichtung per SSH:** HomeCA verbindet sich einmalig mit dem Linux-Gerät (Passwort oder privater Schlüssel, optional `sudo`) und richtet den bestehenden Pull-Agenten selbst ein: Token-Datei, Agent, systemd-Timer oder cron, optional die Root-CA im Systemspeicher, erster Lauf. Zugangsdaten bleiben nur für die Dauer der Anfrage im Speicher und werden nie gespeichert oder protokolliert.
- **Host-Key-Prüfung:** Beim ersten Kontakt zeigt HomeCA den SHA-256-Fingerprint des Host-Keys; erst nach Bestätigung geht es weiter. Der bestätigte Key wird am Ziel gespeichert; ein abweichender Key bricht jede spätere Verbindung ab.
- **Erkennung:** Vor der Einrichtung liest HomeCA nur lesend Hostname, FQDN, Adressen, Init-System, Werkzeuge und laufende Dienste (Proxmox, nginx, HAProxy, Caddy, Traefik, Apache, Jellyfin, UniFi, Portainer, Home Assistant, Synology, Unraid, VMware ESXi u. a.) aus und schlägt Profil, Namen und ein passendes vorhandenes Zertifikat vor.
- **Backups bei jeder Änderung:** Jede Datei, die die Einrichtung oder ein Profilskript auf dem Gerät überschreibt oder anlegt, wird vorher unter `/var/backups/homeca/<Ziel>/<Zeitstempel>-<Anlass>/` gesichert (ohne Agent unter `manual/`). Jedes Backup enthält `files.txt` und ein `restore.sh`, das den vorherigen Zustand wiederherstellt (auch neu angelegte Dateien entfernt). Der Agent behält die letzten zehn Backups je Ziel.
- **Zertifikat im Assistenten:** Passt kein vorhandenes Zertifikat, stellt der Assistent mit vorgeschlagenem Profil und Namen direkt eines aus.
- **Protokoll:** Ausgabe und Exit-Code der Einrichtung erscheinen in der Oberfläche, bereinigt um Token und Passwörter; jede SSH-Sitzung landet im Audit-Log.

## Nicht im Umfang

- Dauerhaftes Push-Verteilen mit gespeichertem SSH-Schlüssel.
- Windows-Ziele und Hersteller-APIs.
- Automatisches Rückgängigmachen; `restore.sh` wird bewusst von Hand ausgeführt.
