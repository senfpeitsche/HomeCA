# Rückmeldung und Protokolle bei der SSH-Einrichtung

## Warum

Eine Windows-Einrichtung endete nach zehn Minuten nur mit „The command did not finish within 10 minutes". Bis dahin zeigte der Assistent nichts, und die bisherige Ausgabe ging beim Abbruch verloren. Weder in HomeCA noch auf dem Gerät war erkennbar, an welcher Stelle die Einrichtung hing.

## Was ändert sich

- **Live-Ausgabe:** HomeCA liest die Ausgabe des Einrichtungsskripts zeilenweise mit. Der Assistent fragt sie während der Installation ab (`GET /api/v1/ssh-setup/progress/{id}`) und zeigt sie mit Uhrzeit unter **Bisherige Ausgabe**. HomeCA ergänzt eigene Schritte (Verbinden, Erkennung, Start und Ende des Skripts).
- **Timeout mit Ausgabe:** Endet ein Befehl nicht rechtzeitig, liefert HomeCA die bis dahin gelesene Ausgabe mit der Meldung zurück.
- **Dienstprotokoll:** Jede Zeile steht auch im Protokoll des HomeCA-Dienstes. Kennwörter, Schlüssel und Token werden vorher entfernt.
- **Protokolle auf Windows:** Einrichtung und Agent schreiben nach `%ProgramData%\HomeCA\Logs` (`setup-<id>-<Zeitstempel>.log`, `agent-<id>.log` mit Ablösung ab 1 MB). Schritte tragen eine Uhrzeit; die Ausgabe von `install.ps1` und Fehlermeldungen landen im Protokoll.
- **SFTP statt Standardeingabe auf Windows:** Das rund 19 KB große Einrichtungsskript kam per Standardeingabe nie vollständig bei PowerShell an. HomeCA lädt es per SFTP ins Benutzerprofil, startet es als Datei und löscht es danach.

## Nicht im Umfang

- Protokolldateien für den Linux-Agenten (er läuft per systemd oder cron, deren Protokolle bereits greifen).
- Abbruch einer laufenden Einrichtung aus dem Assistenten.
