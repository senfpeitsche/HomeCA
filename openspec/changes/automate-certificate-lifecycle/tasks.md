# Aufgaben

## 1. Deployment-Ziele

- [x] Register für Deployment-Ziele mit gehashtem Token, Zertifikatsbezug, Host/Port und Webhook.
- [x] Erneuerung (automatisch und manuell) hängt Ziele und Pläne auf das neue Zertifikat um.
- [x] Abruf-Endpunkte für Manifest, Zertifikatsdateien und Profilskript mit Zieltoken.
- [x] Agent-Skript (Shell) mit Fingerprint-Vergleich, atomarem Austausch und Ausführung des Profilskripts.
- [x] Webhook nach Erneuerung.
- [x] TLS-Prüfung der Zielhosts auf Abruf und täglich im Hintergrund.
- [x] Oberfläche: Ziele anlegen, Token einmalig anzeigen, Agent-Anleitung, Status.

## 2. Vertrauen

- [x] Öffentliche Seite `/trust` mit Betriebssystemerkennung, Anleitungen und QR-Code, auch über HTTP.
- [x] Apple-Konfigurationsprofil `GET /api/v1/trust-anchor/mobileconfig`.
- [x] Name Constraints für neue und rotierte Intermediate-CAs; Prüfung bei der Ausstellung.

## 3. Ausstellung

- [x] CSR signieren (API und Oberfläche), ohne privaten Schlüssel; keine automatische Neuausstellung für CSR-Zertifikate.
- [x] `POST /api/v1/certificates/{id}/renew` und Schaltfläche „Jetzt erneuern“.
- [x] Geräteeingabe aus Hostname und Domain mit DNS-Auflösung; Profilkacheln.

## 4. Anleitungen und Profile

- [x] ACME-Client-Konfigurationen in der Oberfläche.
- [x] Zwölf weitere Zielprofile mit Übersetzungen; neue Standardprofile werden bestehenden Installationen einmalig hinzugefügt.

## 5. Benachrichtigungen und Betrieb

- [x] Kanäle ntfy, Gotify und Webhook mit Testversand; Versand bei Erneuerung, Fehlern, veralteten Zielen und Backup-Fehlern.
- [x] Geplante Backups mit Intervall, Aufbewahrung und optionalem Zweitverzeichnis.
- [x] `GET /api/v1/attention` und Übersicht „Braucht Aufmerksamkeit“.

## 6. Container

- [x] Dockerfile, Entrypoint mit Schlüsselerzeugung, Compose-Datei, `.dockerignore`.

## 7. Qualität

- [x] Tests für alle neuen Dienste und Endpunkte.
- [x] Ressourcen, Hilfe und Dokumentation auf Deutsch und Englisch.
- [x] Ende-zu-Ende-Prüfung in der laufenden Anwendung.
