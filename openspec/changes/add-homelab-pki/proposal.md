# HomeCA: modulare Homelab-PKI

## Warum

Homelab-Dienste und -Geräte verwenden heute zahlreiche unterschiedliche Zertifikatsformate, Vertrauensketten und Einspielwege. Bestehende PKI-Werkzeuge sind entweder CLI-orientiert oder für den gewünschten Alltagsbetrieb zu komplex. HomeCA soll eine einfache, selbst gehostete Verwaltung für interne TLS-, mTLS- und SSH-Zertifikate schaffen und dabei Geräte wie Proxmox, OPNsense, Windows/IIS/RDP, Synology, TeamCity, Home Assistant, UniFi und Netzwerk-Switches unterstützen.

## Was ändert sich

- Eine lokale Webverwaltung auf ASP.NET Core (.NET 10) für Root-, Intermediate-, TLS-, mTLS- und SSH-CAs, betrieben in einem Debian-basierten Proxmox-LXC.
- Ausstellen, erneuern, exportieren und widerrufen von Zertifikaten mit DNS- und IP-SANs sowie Laufzeiten bis zwei Jahre.
- Ein internes ACME-Verzeichnis für alle Namen unter konfigurierten internen Ausstellungszonen.
- Verwaltung externer ACME-Zertifikate über DNS-01.
- Konfigurierbare Technitium- und Hetzner-DNS-Integrationen; keine Instanzdetails wie `zikke.org` werden fest eingebaut.
- Deklarative Zielsystem-Profile mit Formaten, Validierung, Dokumentation und wiederholbaren Einspielskripten.
- Dateibasierter Bestand für langlebige Artefakte sowie ein lokaler eingebetteter Zustandsspeicher; keine externe Datenbank. Betrieb und Konfiguration erfolgen als systemd-Dienst im Debian-LXC.

## Nicht im ersten Umfang

- Ersatz für AD CS, AD-Autoenrollment, SCEP, Intune oder MDM.
- Zwang zu Docker, Cloud-Zugang oder Telemetrie.
- Frei ausführbare Drittanbieter-Plugins mit Zugriff auf CA-Schlüssel.
- OCSP und automatische Remote-Ausführung auf Zielsystemen.

## Entscheidungen

- Die online verfügbare Root-CA ist für den Homelab-Einsatz zulässig; eine TLS-Issuing-CA sowie getrennte SSH-Host- und SSH-User-CAs strukturieren die Vertrauenskette.
- TLS-Zertifikate dürfen maximal 730 Tage gültig sein; Standard ist 365 Tage.
- Neue universelle TLS-Zertifikate nutzen ECC P-256; Legacy-Profile können RSA-2048 oder RSA-3072 verlangen.
- Lokaler Administrator ist der anfängliche Anmeldemechanismus.
- Schlüsselverschlüsselung im Datenspeicher ist optional; verschlüsselte Backups sind verpflichtend.
- CRLs gehören zum ersten Umfang, OCSP nicht.
