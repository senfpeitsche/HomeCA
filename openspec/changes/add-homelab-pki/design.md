# Design: HomeCA

## Architektur

```text
Browser UI -> ASP.NET Core API/Kern (.NET 10) -> CA- und ACME-Dienste
                                -> Dateiartefakte + lokaler Zustand
                                -> Profile und DNS-Connectoren
```

Der Dienst basiert auf ASP.NET Core unter .NET 10 und wird als systemd-Dienst in einem Debian-basierten Proxmox-LXC betrieben. Die React/TypeScript-Oberfläche wird als statisches Asset mit dem Dienst ausgeliefert. Sie spricht eine dokumentierte API; im LXC ist kein Node-Entwicklungsserver und keine separate Datenbank nötig.

## Vertrauensmodell

```text
HomeCA Root (10 Jahre)
├─ TLS Issuing CA (5 Jahre) -> TLS, mTLS, internes ACME
├─ SSH Host CA
└─ SSH User CA

Externes ACME -> DNS-01 über konfigurierten öffentlichen DNS-Provider
```

Die Instanz besitzt konfigurierbare Domains. Für internes ACME darf jedes registrierte Konto Zertifikate für beliebige DNS-Namen unter allen aktivierten internen Ausstellungszonen beziehen, aber nie für Namen außerhalb dieser Zonen.

## Daten und Schlüssel

CA-Objekte, Zertifikate, Exportpakete, Profil-Snapshots, CRLs und Audit-Ereignisse liegen in einer nachvollziehbaren Verzeichnisstruktur. Transaktionaler Zustand für Seriennummern, ACME-Konten, Orders und Nonces wird lokal eingebettet gespeichert. Daten- und Secret-Verzeichnisse sind über die systemd-Konfiguration des Debian-LXC explizit konfigurierbar. Die CA-Schlüssel können unverschlüsselt, mit einem LXC-Secret umschlossen oder nach Neustart passwortgeschützt betrieben werden. Backups werden immer separat symmetrisch verschlüsselt.

## Erweiterungen

Zielprofile sind deklarativ und erhalten keinen Private-Key-Zugriff. Sie bestimmen beantragte Zertifikatsparameter innerhalb der Kern-Policy, Exporte, Validierungen sowie versionsierte Hilfetexte und Skriptvorlagen. Connectoren handeln nach außen und werden mit einzeln gespeicherten Secrets konfiguriert.

Technitium ist zugleich DNS-Connector und Zielprofil für sein eigenes Web-UI-Zertifikat. Hetzner ist ein DNS-01-Connector; beide sind nur Beispiele austauschbarer Integrationen.

## Bedienung und Dokumentation

Die UI besitzt Übersicht, Zertifikate, CAs, Vertrauen, Automatik und Einstellungen. Jeder Ausstellungsprozess beginnt mit Ziel und Zweck. Das erzeugte Deployment-Paket enthält Zertifikatsexporte, Prüfsummen, Anleitung sowie Einspiel-/Erneuerungsskripte. Zertifikate speichern einen unveränderlichen Snapshot der verwendeten Profil- und Dokumentationsversion, damit die passende Anleitung auch nach längerer Zeit verfügbar bleibt.
