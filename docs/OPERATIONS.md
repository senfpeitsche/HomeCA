# HomeCA-Betrieb

## Prüfung nach der Bereitstellung

1. Prüfe `GET /health` auf eine erfolgreiche Antwort.
2. Richte über den nur lokal erreichbaren Setup-Endpunkt den Administrator ein und melde dich an.
3. Initialisiere die CAs und stelle ein Testzertifikat für eine interne Ausstellungszone aus.
4. Prüfe die PEM-Kette mit `openssl verify` gegen die exportierte Root-CA.
5. Führe für jeden DNS-Connector zuerst den Berechtigungstest und dann den TXT-Test aus.

## Ablaufwarnungen

`GET /api/v1/warnings/expiring` liefert Zertifikate, die innerhalb von 30 Tagen ablaufen. Der Abruf sollte mindestens täglich über einen lokalen Monitoring-Job erfolgen; kritische Warnungen werden vor Ablauf an den Administrator weitergegeben.

## E-Mail-Benachrichtigungen für die Erneuerung

Unter **Erneuerungsautomatik** können E-Mail-Benachrichtigungen aktiviert werden. HomeCA sendet dann eine Nachricht, wenn ein Zertifikat automatisch erneuert wurde oder wenn eine automatische Erneuerung fehlschlägt. Über **Test-E-Mail senden** lässt sich die Konfiguration vor dem Produktiveinsatz prüfen.

- **SMTP:** Funktioniert mit jedem TLS-geschützten SMTP-Server. Für Microsoft 365 SMTP lautet der Server üblicherweise `smtp.office365.com` mit Port `587`; SMTP-Authentifizierung muss für das Absenderpostfach erlaubt sein.
- **Microsoft 365 über Graph:** Die App-Registrierung benötigt die Microsoft-Graph-Anwendungsberechtigung `Mail.Send` und einen erteilten Administrator-Consent. Tenant-ID, Client-ID, Client-Secret und Absenderpostfach werden in HomeCA hinterlegt.

Kennwörter und Client-Secrets werden nur bei der Eingabe verarbeitet und nicht über die Verwaltungs-API zurückgeliefert. Die gespeicherte Konfiguration liegt im geschützten HomeCA-Datenverzeichnis; dessen Berechtigungen müssen auf den Dienstbenutzer beschränkt bleiben.

## Backup und Restore

Erzeuge ein verschlüsseltes Backup über `POST /api/v1/backups`. Prüfe es anschließend mit `POST /api/v1/backups/{fileName}/verify`; die Prüfung entschlüsselt das Archiv und liest dessen ZIP-Inhalt ohne den laufenden Zustand zu verändern.

Für eine Wiederherstellung den Dienst anhalten, das aktuelle Datenverzeichnis separat sichern, das geprüfte HCAB1-Archiv mit dem 32-Byte-Backup-Schlüssel entschlüsseln und in ein leeres Datenverzeichnis entpacken. Dateibesitzer wieder auf `homeca:homeca` setzen, Dienst starten und anschließend `/health`, CA-Inventar und eine Testkette prüfen. Niemals in ein laufendes Datenverzeichnis zurückspielen.

## Debian-LXC

Der LXC benötigt .NET 10, OpenSSH-Client für SSH-CAs und schreibbaren Speicher für `/var/lib/homeca` sowie `/var/backups/homeca`. Die unit unter `deploy/systemd/homeca.service` verwaltet den Dienst. Vor der Produktionsfreigabe Berechtigungen der Daten- und Backup-Schlüsseldateien auf den Dienstbenutzer beschränken.
