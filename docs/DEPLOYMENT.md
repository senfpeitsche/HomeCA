# Zertifikate automatisch verteilen

Ein **Verteilungsziel** folgt einem Zertifikat über alle Erneuerungen hinweg. Stellt HomeCA einen Nachfolger aus – automatisch über einen Erneuerungsplan oder über **Jetzt erneuern** –, zeigt das Ziel sofort auf das neue Zertifikat. Ein kleiner Agent auf dem Gerät holt die Dateien ab und führt das Profilskript aus.

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
