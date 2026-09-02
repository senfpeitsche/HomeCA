# HomeCA-Betrieb

## Prüfung nach der Bereitstellung

1. Prüfe `GET /health` auf eine erfolgreiche Antwort.
2. Richte den Administrator aus einem vertrauenswürdigen Admin-Netz ein und melde dich an. Nach der Ersteinrichtung TLS aktivieren und den Zugriff mit Firewall oder Reverse Proxy begrenzen.
3. Initialisiere die CAs und stelle ein Testzertifikat für eine interne Ausstellungszone aus.
4. Prüfe die PEM-Kette mit `openssl verify` gegen die exportierte Root-CA.
5. Führe für jeden DNS-Connector zuerst den Berechtigungstest und dann den TXT-Test aus.

## Ablaufwarnungen

`GET /api/v1/warnings/expiring` liefert Zertifikate, die innerhalb von 30 Tagen ablaufen. Der Abruf sollte mindestens täglich über einen lokalen Monitoring-Job erfolgen; kritische Warnungen werden vor Ablauf an den Administrator weitergegeben.

## Intermediate-CA rotieren

Eine regulär ablaufende TLS-Intermediate wird **nicht** gesperrt. Der Administrator erstellt rechtzeitig eine Ersatz-Intermediate unter derselben Root-CA, macht sie zur Ausstellungs-CA und verteilt alle danach ausgestellten oder erneuerten Zertifikate samt neuer Kette. Die alte Intermediate bleibt deaktiviert, aber verfügbar, bis das letzte von ihr ausgestellte Zertifikat abgelaufen oder ersetzt ist. Die Root-CA bleibt unverändert; sie muss nicht neu verteilt werden.

Eine neue Intermediate muss vor ihrer Root-CA ablaufen. Ebenso darf ein TLS-Zertifikat nicht über das Ablaufdatum seiner ausstellenden Intermediate hinausreichen. Plane die Rotation daher mindestens so früh, wie die längste zulässige TLS-Zertifikatslaufzeit (derzeit 730 Tage) beträgt.

Jede Intermediate besitzt eine eigene CRL unter `GET /api/v1/crl/<authority-id>`. Diese Adresse wird in neu ausgestellte Zertifikate eingebettet. Zertifikate, die vor dieser Umstellung die allgemeine Adresse `GET /api/v1/crl/latest` erhielten, sollten während der Rotation erneuert und ausgerollt werden.

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
