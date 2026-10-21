## ADDED Requirements

### Requirement: Profilgerechtes Einspielskript

Das System MUSS das Einspielskript eines Deployment-Pakets in dem Format liefern, das zum Zielsystem passt.

#### Scenario: Linux-Zielsystem

- **WHEN** ein Zertifikat für ein Profil mit Skripttyp `sh` ausgestellt wird
- **THEN** MUSS das Paket `install.sh` mit Shebang und `set -eu` enthalten und KEIN `install.ps1`

#### Scenario: Gerät mit eigener CLI

- **WHEN** ein Zertifikat für ein Profil mit Skripttyp `cli` ausgestellt wird
- **THEN** MUSS das Paket `device-commands.txt` enthalten, das ausdrücklich nicht als Skript gekennzeichnet ist

#### Scenario: Profil ohne Skripttyp

- **WHEN** ein Profil keinen Skripttyp angibt
- **THEN** MUSS das System PowerShell an typischen Cmdlets erkennen und sonst ein Shell-Skript liefern

### Requirement: Erneuerung standardmäßig aktiv

Das System MUSS beim Ausstellen über die Verwaltungs-API einen Erneuerungsplan anlegen, sofern der Aufrufer dies nicht abwählt.

#### Scenario: Ausstellung mit Standardwerten

- **WHEN** ein Zertifikat mit 90 Tagen Laufzeit ohne weitere Angaben ausgestellt wird
- **THEN** MUSS ein aktiver Erneuerungsplan mit 30 Tagen Vorlauf entstehen und seine ID in der Antwort stehen

#### Scenario: Ungültiges Erneuerungsfenster

- **WHEN** das angegebene Erneuerungsfenster nicht kürzer als die Laufzeit ist
- **THEN** MUSS die Anfrage abgelehnt werden, bevor ein Zertifikat ausgestellt wird

### Requirement: Lokalisierte Zielprofile

Das System MUSS Name, Zweck und Anleitung der Standardprofile in jeder unterstützten Sprache anzeigen und auf den englischen Basistext zurückfallen.

#### Scenario: Bestehende Installation

- **WHEN** eine ältere Installation ein unverändertes Standardprofil gespeichert hat
- **THEN** MUSS das System es durch die aktuelle, lokalisierte Fassung ersetzen und bearbeitete Profile unverändert lassen
