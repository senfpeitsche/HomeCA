# Aufgaben

## 1. Agent

- [x] `agent.ps1`: Manifest, Download, Fingerprint, Austausch, `install.ps1`, Sperre, Backups mit Aufbewahrung, KettenprÃ¼fung gegen eigene Root-CA.
- [x] Endpunkte `GET /api/v1/deploy/{id}/agent.ps1` und `GET /api/v1/deployment-targets/{id}/agent?platform=windows`.
- [x] `certificate.pfx` fÃ¼r Agenten, geschÃ¼tzt mit dem Ziel-Token.

## 2. Profile und Backups

- [x] PowerShell-Kopf mit `Backup-HomeCAFile`, `Add-HomeCARestoreStep` und `restore.ps1`.
- [x] `iis` unbeaufsichtigt mit Umbinden und RÃ¼ckweg; Migration der unverÃ¤nderten Vorversion.
- [x] Neues Profil `windows-certstore`.

## 3. Einrichtung

- [x] Windows-Erkennung Ã¼ber OpenSSH, auch wenn `sh` aus Git fÃ¼r Windows vorhanden ist.
- [x] Einrichtungsskript: Backup, ACLs, Token, Agent, Root-CA optional, geplante Aufgabe, erster Lauf.
- [x] PowerShell-Befehle fÃ¼r manuelle Einrichtung ohne OpenSSH.

## 4. OberflÃ¤che

- [x] Assistent fÃ¼r Windows (Push aus, Vertrauensspeicher Windows), Agent- und Einrichtungsbefehle fÃ¼r Windows in der Zielliste.

## 5. QualitÃ¤t

- [x] Tests fÃ¼r Skripte, Erkennung, PFX und Ablauf mit Test-Sitzung.
- [x] Lokale PrÃ¼fung von Agent, Backups und Bootstrap mit Windows PowerShell.
- [x] Ressourcen, Doku und Bedrohungsmodell auf Deutsch und Englisch.
