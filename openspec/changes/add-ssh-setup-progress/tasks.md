# Aufgaben

## 1. Server

- [x] SSH-Befehle lesen stdout und stderr zeilenweise mit; bei Timeout bleibt die Ausgabe erhalten.
- [x] `SshSetupProgress` im Speicher, Abruf über `GET /api/v1/ssh-setup/progress/{id}`, IDs nur 32 Hex-Zeichen.
- [x] Einrichtung meldet eigene Schritte und jede Zeile an Assistent und Dienstprotokoll, ohne Geheimnisse.

## 2. Oberfläche

- [x] Assistent zeigt **Bisherige Ausgabe** während der Installation und nach dem Ende, Hinweis auf die Windows-Protokolle bei Fehlern.

## 3. Windows

- [x] Einrichtung und Agent schreiben Protokolle nach `%ProgramData%\HomeCA\Logs`, Schritte mit Uhrzeit, Ausgabe von `install.ps1` und Fehler eingeschlossen.

## 4. Prüfung

- [x] Unit-Tests für Fortschritt, Timeout-Ausgabe ohne Token und ungültige IDs.
- [x] SSH-Ebene gegen einen echten OpenSSH-Server: Zeilen kommen live an, Teilausgabe bei Timeout.
- [x] Windows-Agent unter PowerShell 5.1 gegen einen Testserver: Protokoll, Uhrzeiten, Fehlerfall.
- [x] Live-Ausgabe im Assistenten im Browser gegen einen Test-Container.
- [x] Doku DEPLOYMENT (de/en).
- [ ] Windows-Einrichtung auf dem Server des Betreibers wiederholen und Ursache des Hängers anhand der Protokolle klären (extern).
