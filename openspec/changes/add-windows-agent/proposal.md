# Windows-Agent und Einrichtung über OpenSSH

## Warum

Verteilungsziele funktionieren bisher nur auf Geräten mit POSIX-Shell. Windows-Server mit IIS, RDP oder eigenen Diensten müssen erneuerte Zertifikate von Hand importieren und binden.

## Was ändert sich

- **PowerShell-Agent:** `agent.ps1` für Windows PowerShell 5.1 und PowerShell 7. Er holt Manifest und Dateien mit dem Ziel-Token, prüft den Fingerprint, tauscht `current`/`previous`, führt `install.ps1` aus und läuft stündlich als geplante Aufgabe unter SYSTEM. Parallele Läufe schließt eine Sperrdatei aus (Exit-Code 75). Vertraut Windows der HomeCA-Root-CA nicht, prüft der Agent die Kette gegen eine mitgelieferte Kopie.
- **PFX für den Agenten:** Der Agent erhält `certificate.pfx` mit Zertifikat, Schlüssel und ausstellender CA, geschützt mit dem Ziel-Token; `install.ps1` bekommt das Kennwort über `HOMECA_PFX_PASSWORD`.
- **Einrichtung über OpenSSH:** Die SSH-Einrichtung erkennt Windows (cmd oder PowerShell als Standard-Shell, auch mit Git-Bash im PATH) und richtet den PowerShell-Agenten ein. Das Skript kommt über die Standardeingabe; HomeCA verlangt ein Administratorkonto.
- **Erkennung:** Hostname, FQDN, Adressen, Windows-Version, Administratorrechte und IIS; Vorschlag des Profils `iis` oder `windows-certstore`.
- **Backups:** Vor jeder Änderung sichert HomeCA Dateien, Aufgabe, Zertifikatsspeicher-Einträge und IIS-Bindungen unter `%ProgramData%\HomeCA\Backups\<Ziel>\<Zeitstempel>-<Anlass>\` mit `restore.ps1`. Profile nutzen dafür `Backup-HomeCAFile` und `Add-HomeCARestoreStep`. Der Agent behält zehn Backups.
- **Profile:** `iis` läuft unbeaufsichtigt, importiert das PFX in `LocalMachine\My` und bindet HTTPS-Bindungen um, die ein Zertifikat mit gleichem Subject nutzen. Neues Profil `windows-certstore` importiert nur in den Speicher.
- **Ohne OpenSSH:** Für jedes Ziel zeigt HomeCA PowerShell-Befehle mit Token, die ein Administrator lokal oder über eine eigene Remoting-Sitzung (WinRM) ausführt.

## Nicht im Umfang

- Einrichtung direkt über WinRM: Von Linux aus gibt es keinen gepflegten Client, und ohne Kerberos oder HTTPS-Listener fehlt eine Server-Authentisierung wie der SSH-Host-Key.
- Push auf Windows-Ziele.
