## ADDED Requirements

### Requirement: Geführte Einrichtung per SSH

Das System MUSS ein Verteilungsziel über eine einmalige SSH-Verbindung einrichten können, ohne Zugangsdaten zu speichern.

#### Scenario: Unbekannter Host-Key

- **WHEN** HomeCA sich mit einem Host verbindet und kein bestätigter Fingerprint vorliegt
- **THEN** MUSS das System vor jeder Authentisierung abbrechen und den SHA-256-Fingerprint des Host-Keys zur Bestätigung zurückgeben

#### Scenario: Geänderter Host-Key

- **WHEN** der Host-Key eines Ziels vom gespeicherten Fingerprint abweicht
- **THEN** MUSS das System die Verbindung abbrechen und keine Zugangsdaten senden

#### Scenario: Zugangsdaten

- **WHEN** eine Erkennung oder Einrichtung abgeschlossen ist
- **THEN** DÜRFEN Passwort, privater Schlüssel, Passphrase und Ziel-Token weder gespeichert noch in Protokoll, Audit-Log oder Antwort enthalten sein

#### Scenario: Erfolgreiche Einrichtung

- **WHEN** die Einrichtung auf einem Linux-Host durchläuft
- **THEN** MUSS der Agent mit Token und Zeitsteuerung installiert, einmal ausgeführt und das Ergebnis mit Exit-Code angezeigt werden

### Requirement: Erkennung des Zielsystems

Das System MUSS vor der Einrichtung lesend erkennen, welche Dienste laufen, und daraus Profil und Namen vorschlagen.

#### Scenario: Proxmox erkannt

- **WHEN** auf dem Host `/etc/pve` existiert
- **THEN** MUSS das Profil `proxmox` vorgeschlagen und der FQDN als Name angeboten werden

### Requirement: Backups vor Änderungen auf dem Zielsystem

Das System MUSS jede Datei sichern, die Einrichtung oder Profilskript auf dem Zielsystem überschreibt oder anlegt.

#### Scenario: Profilskript überschreibt Zertifikat

- **WHEN** ein Profilskript eine bestehende Datei per `cp` oder `install` ersetzt
- **THEN** MUSS vorher eine Kopie im Backup-Verzeichnis liegen und `restore.sh` sie zurückspielen können

#### Scenario: Neu angelegte Datei

- **WHEN** Einrichtung oder Profilskript eine bisher nicht vorhandene Datei anlegt
- **THEN** MUSS `restore.sh` diese Datei beim Wiederherstellen entfernen

#### Scenario: Aufbewahrung

- **WHEN** der Agent für ein Ziel mehr als zehn Backups angelegt hat
- **THEN** MUSS er die ältesten über diese Zahl hinaus löschen
