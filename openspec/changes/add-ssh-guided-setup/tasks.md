# Aufgaben

## 1. Backups auf dem Zielsystem

- [x] Shell-Profilskripte erhalten Helfer `homeca_backup`; `cp` und `install` sichern das Ziel vor dem Überschreiben automatisch.
- [x] Backup-Verzeichnis mit `files.txt` und `restore.sh`, das überschriebene Dateien zurückspielt und neu angelegte entfernt.
- [x] Mitgelieferte Profile, die mit Umleitung, `openssl -out` oder `keytool` schreiben, rufen `homeca_backup` ausdrücklich auf; unveränderte Vorgängerversionen werden migriert.
- [x] Agent setzt `HOMECA_BACKUP_DIR` je Lauf und behält die letzten zehn Backups.

## 2. SSH-Verbindung

- [x] SSH.NET-Anbindung hinter einer austauschbaren Sitzungsschnittstelle (Passwort, privater Schlüssel mit Passphrase).
- [x] Host-Key-Prüfung mit Bestätigung beim ersten Kontakt und Abbruch bei Abweichung vom gespeicherten Key.
- [x] `sudo` mit und ohne Passwort; root ohne `sudo`.
- [x] Zugangsdaten nie speichern oder protokollieren; Ausgabe um Token und Passwörter bereinigen.

## 3. Erkennung

- [x] Lesendes Erkennungsskript für Hostname, FQDN, Adressen, Init-System, Werkzeuge, Trust-Store und Dienste.
- [x] Profil-, Namens- und Zertifikatsvorschläge aus den erkannten Fakten.

## 4. Einrichtung

- [x] Einrichtungsskript: Backup, Token, Agent, systemd-Timer oder cron, optional Root-CA, erster Lauf.
- [x] Neues Ziel anlegen oder bestehendes Ziel neu einrichten (Token wird dabei erneuert); SSH-Metadaten ohne Geheimnisse am Ziel speichern.
- [x] TLS-Prüfung direkt nach der Einrichtung, wenn ein Host gesetzt ist.
- [x] Audit-Einträge für Erkennung und Einrichtung.

## 5. API und Oberfläche

- [x] `POST /api/v1/ssh-setup/detect` und `POST /api/v1/ssh-setup/install`.
- [x] Zertifikat im Assistenten ausstellen, wenn kein passendes vorhanden ist.
- [x] Assistent unter **Verteilung**: Verbindung, Host-Key bestätigen, Vorschläge, Einrichtung mit Protokoll.

## 6. Qualität

- [x] Tests für Skripte, Erkennung, Host-Key-Logik, Bereinigung und Ablauf mit Test-Sitzung.
- [x] Ressourcen, Hilfe und Dokumentation auf Deutsch und Englisch; Bedrohungsmodell ergänzen.
- [x] Ende-zu-Ende-Prüfung gegen einen echten SSH-Server (Container).
