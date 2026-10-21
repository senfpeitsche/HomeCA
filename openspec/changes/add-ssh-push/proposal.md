# Sofort verteilen per SSH-Push

## Warum

Nach einer Erneuerung holt der Agent das neue Zertifikat erst beim nächsten stündlichen Lauf ab. Bei „Jetzt erneuern“ oder nach einem Fehler will man das Ergebnis sofort sehen. HomeCA soll den Agenten deshalb direkt anstoßen können, ohne dauerhaft Passwörter oder einen allgemein nutzbaren Schlüssel zu speichern.

## Was ändert sich

- **Eigener Push-Schlüssel:** HomeCA erzeugt einmalig ein ECDSA-P-256-Schlüsselpaar. Der private Schlüssel liegt verschlüsselt mit dem CA-Schlüssel im Datenverzeichnis und verlässt HomeCA nie.
- **Eingeschränkter Zugang:** Die SSH-Einrichtung trägt auf Wunsch den öffentlichen Schlüssel in `/root/.ssh/authorized_keys` ein, mit `command="<Agent>"` und `no-pty`, `no-port-forwarding`, `no-agent-forwarding`, `no-X11-forwarding`. Der Schlüssel kann nur den Agenten des Ziels starten, keine Shell und keine Weiterleitung. Lehnt `sshd` root-Anmeldungen ab, bleibt Push aus und es gilt nur der Timer.
- **Backup und Rückweg:** Die Einrichtung sichert `authorized_keys` vorher. `restore.sh` entfernt gezielt nur die HomeCA-Zeile, damit später ergänzte Schlüssel erhalten bleiben.
- **Push nach Erneuerung:** Nach jeder Erneuerung verbindet sich HomeCA mit gespeichertem Host-Key und Push-Schlüssel und startet den Agenten. Ergebnis, Exit-Code und Zeitpunkt stehen am Ziel; ein Fehler erzeugt die neue Benachrichtigung „Push fehlgeschlagen“. Der Timer bleibt als Rückfallweg aktiv.
- **Jetzt verteilen:** Schaltfläche und `POST /api/v1/deployment-targets/{id}/push` stoßen den Agenten manuell an.
- **Push abschalten:** Am Ziel lässt sich Push in HomeCA abschalten; die Zeile auf dem Gerät entfernt `restore.sh` oder eine erneute Einrichtung ohne Push.
- **Agent-Sperre:** Gleichzeitige Läufe (Timer und Push) schließen sich über ein Sperrverzeichnis aus.

## Nicht im Umfang

- Push für Nicht-root-Benutzer über sudo.
- Rotation des Push-Schlüssels.
- Windows-Ziele und Hersteller-APIs.
