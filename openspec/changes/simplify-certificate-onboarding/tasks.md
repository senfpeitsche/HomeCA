# Aufgaben

## 1. Skripttyp je Profil

- [x] Skripttyp `sh`/`ps1`/`cli` im Profilmodell, in der API-Validierung und im Profilformular ergänzen.
- [x] Deployment-Paket schreibt `install.sh` (mit `set -eu`), `install.ps1` (mit `$ErrorActionPreference = 'Stop'`) oder `device-commands.txt` und nennt die Datei im Paket-README und im Profil-Snapshot.
- [x] Erkennung für Profile ohne Skripttyp (PowerShell-Cmdlets, sonst Shell).

## 2. Erneuerung standardmäßig aktiv

- [x] `POST /api/v1/certificates` legt einen Erneuerungsplan an, außer bei `autoRenew: false`; Fenster 30 Tage bzw. ein Drittel kürzerer Laufzeiten, eigenes Fenster wird vor der Ausstellung geprüft.
- [x] Antwort enthält `renewalPlanId`; die Oberfläche zeigt eine Warnung, wenn kein Plan angelegt werden konnte.
- [x] Option „Vor Ablauf automatisch erneuern“ im Ausstellungsassistenten, vorausgewählt.

## 3. Lokalisierte Profiltexte

- [x] Englische Basistexte und deutsche Übersetzungen für alle Standardprofile.
- [x] Oberfläche zeigt die Übersetzung der aktiven Sprache mit Rückfall auf den Basistext; das Bearbeitungsformular bearbeitet den Basistext.
- [x] Beim Ändern eines Basistexts wird nur dessen veraltete Übersetzung verworfen.
- [x] Migration unveränderter Standardprofile älterer Releases anhand von Fingerprints.

## 4. Qualität

- [x] Tests für Skripttyp, Paketinhalt, Migration, Übersetzungsabdeckung und automatische Erneuerungspläne.
- [x] Englische und deutsche Ressourcen, Hilfe und Dokumentation aktualisieren.
