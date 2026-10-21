## ADDED Requirements

### Requirement: Windows-Agent

Das System MUSS einen PowerShell-Agenten bereitstellen, der erneuerte Zertifikate mit dem Ziel-Token abholt und über das Profilskript installiert.

#### Scenario: Erneuerung auf Windows

- **WHEN** sich der Fingerprint im Manifest ändert
- **THEN** MUSS der Agent die Dateien laden, den Fingerprint prüfen, `install.ps1` mit `HOMECA_PFX_PASSWORD` ausführen und vorher ersetzte Bindungen und Dateien in einem Backup mit `restore.ps1` festhalten

#### Scenario: Paralleler Lauf

- **WHEN** ein zweiter Agentenlauf startet, während der erste läuft
- **THEN** MUSS der zweite mit Exit-Code 75 enden, ohne etwas zu ändern

### Requirement: Windows-Einrichtung über OpenSSH

Das System MUSS Windows-Ziele über OpenSSH erkennen und einrichten und dabei dieselben Garantien wie für Linux einhalten: bestätigter Host-Key, keine gespeicherten Zugangsdaten, Backup vor jeder Änderung.

#### Scenario: Kein Administrator

- **WHEN** der angemeldete Benutzer keine Administratorrechte hat
- **THEN** MUSS die Einrichtung abbrechen, bevor ein Ziel angelegt oder ein Token erneuert wird
