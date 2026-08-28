# HomeCA Homelab PKI

## ADDED Requirements

### Requirement: Konfigurierbare Instanz- und Domainverwaltung

Das System MUSS mehrere Domains und DNS-Integrationen konfigurierbar verwalten, ohne konkrete Domains oder Anbieter als Instanzannahme fest einzubauen.

#### Scenario: Interne Zone aktivieren

- **WHEN** ein Administrator eine Domain als interne Ausstellungszone aktiviert
- **THEN** darf die interne CA und der interne ACME-Dienst Zertifikate nur für Namen unter dieser Zone ausstellen

#### Scenario: DNS-Provider zuordnen

- **WHEN** ein Administrator einer Domain eine DNS-Integration zuordnet
- **THEN** MUSS das System Verbindung und verfügbaren Zonenzugriff prüfen können

### Requirement: PKI-Hierarchie und Zertifikatsausstellung

Das System MUSS eine Root-CA, mindestens eine TLS-Issuing-CA sowie getrennte SSH-Host- und SSH-User-CAs verwalten.

#### Scenario: TLS-Zertifikat ausstellen

- **WHEN** ein berechtigter Nutzer ein TLS-Zertifikat für einen zulässigen Namen beantragt
- **THEN** MUSS das Zertifikat DNS-SANs, IP-SANs, den gewählten Schlüsseltyp und eine Laufzeit bis maximal 730 Tage enthalten können

#### Scenario: Legacy-Profil erzwingt RSA

- **WHEN** ein Zielprofil RSA verlangt
- **THEN** MUSS das System ein RSA-Zertifikat gemäß der Profilpolitik erzeugen und die abweichende Wahl sichtbar machen

### Requirement: Internes und externes ACME

Das System MUSS ein internes ACME-Verzeichnis und verwaltete externe ACME-Issuer unterstützen.

#### Scenario: Internes ACME für zulässigen Namen

- **WHEN** ein registriertes internes ACME-Konto ein Zertifikat für einen Namen unter einer aktivierten internen Ausstellungszone bestellt
- **THEN** MUSS das System die Bestellung zulassen und über die TLS-Issuing-CA ausstellen

#### Scenario: Externes DNS-01

- **WHEN** ein externer ACME-Issuer DNS-01 benötigt
- **THEN** MUSS das System den Challenge-TXT-Eintrag über den zugeordneten öffentlichen DNS-Connector verwalten können

### Requirement: Deklarative Zielprofile und Deployment-Pakete

Das System MUSS Zielsystem-Profile ohne ausführbaren Drittcode laden können.

#### Scenario: Zielprofil auswählen

- **WHEN** ein Nutzer ein Zielprofil auswählt
- **THEN** MUSS die UI passende Standardparameter, Validierungen, Exporte und kontextbezogene Dokumentation darstellen

#### Scenario: Deployment langfristig nachvollziehen

- **WHEN** ein Zertifikat über ein Zielprofil ausgestellt wird
- **THEN** MUSS dessen Deployment-Paket einen versionsierten Snapshot von Anleitung, Skripten und Profilinformationen enthalten

### Requirement: Dokumentation im Kontext

Das System MUSS Hilfe sowohl während der Konfiguration als auch im Kontext eines bestehenden Zertifikats zugänglich machen.

#### Scenario: Erneuerung nach langer Zeit

- **WHEN** ein Nutzer ein früher ausgestelltes Zertifikat öffnet
- **THEN** MUSS er die beim letzten Deployment verwendete Anleitung und die zugehörigen Skripte erneut abrufen können

### Requirement: Lokaler Betrieb und Schutz

Das System MUSS ohne externe Datenbank und ohne Cloudabhängigkeit in einem Proxmox-LXC betreibbar sein.

#### Scenario: Verschlüsseltes Backup

- **WHEN** ein Administrator ein Backup erzeugt
- **THEN** MUSS das Backup unabhängig von der optionalen Laufzeit-Schlüsselverschlüsselung symmetrisch verschlüsselt sein

#### Scenario: Lokale Administration

- **WHEN** eine neue Instanz eingerichtet wird
- **THEN** MUSS sie einen lokalen Administrator zur Anmeldung einrichten können
