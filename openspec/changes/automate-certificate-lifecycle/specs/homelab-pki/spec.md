## ADDED Requirements

### Requirement: Deployment-Ziele mit Agent

Das System MUSS Verteilungsziele verwalten, die einem Zertifikat über Erneuerungen folgen und mit einem zielgebundenen Token abgerufen werden.

#### Scenario: Erneuerung hängt Ziele um

- **WHEN** ein Zertifikat mit Verteilungsziel automatisch oder per „Jetzt erneuern“ erneuert wird
- **THEN** MUSS das Ziel auf den Nachfolger zeigen, ein konfigurierter Webhook eine Nachricht ohne Geheimnisse erhalten und der Agent beim nächsten Lauf den neuen Fingerprint sehen

#### Scenario: Token-Schutz

- **WHEN** ein Agent-Endpunkt ohne oder mit falschem Token aufgerufen wird
- **THEN** MUSS das System die Anfrage ablehnen; gespeichert wird nur ein Hash des Tokens

#### Scenario: Ausgeliefertes Zertifikat prüfen

- **WHEN** HomeCA ein Ziel mit Host per TLS prüft
- **THEN** MUSS das Ergebnis „aktuell“, „veraltet“, „fremd“ oder „nicht erreichbar“ lauten und ein Wechsel weg von „aktuell“ benachrichtigt werden

### Requirement: Vertrauen einfach herstellen

Das System MUSS eine öffentliche, über HTTP erreichbare Trust-Seite und ein Apple-Konfigurationsprofil bereitstellen und Intermediate-CAs auf eigene Namen beschränken können.

#### Scenario: Gerät ohne Vertrauen

- **WHEN** ein Gerät `/trust` über HTTP öffnet
- **THEN** MUSS die Seite die Anleitung für das erkannte Betriebssystem öffnen, Fingerprints und einen QR-Code zeigen

#### Scenario: Name Constraints

- **WHEN** die ausstellende CA auf Domains oder Netze beschränkt ist und ein Zertifikat außerhalb davon beantragt wird
- **THEN** MUSS HomeCA die Ausstellung ablehnen

### Requirement: Ausstellung für alle Gerätearten

Das System MUSS CSRs signieren, ohne Erweiterungen aus dem CSR zu übernehmen, und Zertifikate auf Wunsch sofort erneuern.

#### Scenario: CSR signieren

- **WHEN** ein gültig signierter CSR mit RSA ≥ 2048 Bit oder EC ≥ P-256 eingereicht wird
- **THEN** MUSS HomeCA ein Blattzertifikat mit den angefragten oder angegebenen Namen ausstellen und keinen privaten Schlüssel exportieren

### Requirement: Betrieb überwachen

Das System MUSS geplante Backups mit Aufbewahrung ausführen und offene Probleme gesammelt anzeigen.

#### Scenario: Zweites Backup-Verzeichnis fehlt

- **WHEN** das konfigurierte zweite Verzeichnis zum Zeitpunkt des Backups nicht existiert
- **THEN** MUSS der Lauf als fehlgeschlagen gelten, in der Übersicht erscheinen und benachrichtigt werden

#### Scenario: Braucht Aufmerksamkeit

- **WHEN** ein Zertifikat innerhalb von 30 Tagen ohne Erneuerungsplan abläuft und kein neueres Zertifikat mit demselben Subject existiert
- **THEN** MUSS `GET /api/v1/attention` einen Eintrag dafür liefern
