# Zertifikatslebenszyklus automatisieren

## Warum

Nach den Quick Wins bleibt die größte Hürde der Teil nach der Ausstellung: Erneuerte Zertifikate kommen nicht von selbst auf die Geräte, die Root-CA muss auf jedem Client mühsam installiert werden, und Fehler fallen erst auf, wenn ein Browser warnt. Dazu fehlen Wege für Geräte, die ihren Schlüssel selbst erzeugen, Anleitungen für ACME-Clients, verbreitete Homelab-Ziele, moderne Benachrichtigungskanäle, regelmäßige Backups und ein einfacher Container-Betrieb.

## Was ändert sich

- **Deployment-Ziele:** Ein Ziel folgt einem Zertifikat über alle Erneuerungen. Ein Agent auf dem Gerät holt die aktuellen Dateien über eine feste URL mit eigenem Token ab und führt das Profilskript aus. Optional benachrichtigt ein Webhook das Ziel nach einer Erneuerung. HomeCA prüft per TLS-Verbindung, ob das Gerät das aktuelle Zertifikat ausliefert.
- **Trust-Seite:** `/trust` ist ohne Anmeldung und über HTTP erreichbar, erkennt das Betriebssystem, bietet ein Apple-Konfigurationsprofil, Skripte und Anleitungen sowie einen QR-Code.
- **Name Constraints:** Neue Intermediate-CAs können auf eigene Domains und Netze beschränkt werden; HomeCA verweigert Zertifikate außerhalb dieser Grenzen.
- **Ausstellung:** CSR signieren, „Jetzt erneuern“, Geräteeingabe aus Hostname und Domain mit DNS-Auflösung und Profilauswahl als Kacheln.
- **ACME-Anleitungen:** Kopierfertige Konfigurationen für certbot, acme.sh, Caddy, Traefik und Proxmox.
- **Profile:** FRITZ!Box, TrueNAS, pfSense, Unraid, Traefik, Caddy, Nginx Proxy Manager, Portainer, VMware ESXi, Dell iDRAC, Nextcloud (Apache) und Jellyfin.
- **Benachrichtigungen:** ntfy, Gotify und generische Webhooks (kompatibel mit Slack, Discord, Mattermost) zusätzlich zu E-Mail.
- **Betrieb:** Geplante Backups mit Aufbewahrung und optionalem Zweitziel; eine Übersicht „Braucht Aufmerksamkeit“ in UI und API.
- **Container:** Dockerfile und Compose-Datei.

## Nicht im Umfang

- Aktives Verteilen per SSH oder über Hersteller-APIs; HomeCA speichert keine Zugangsdaten zu Zielsystemen.
- Signierte Apple-Konfigurationsprofile.
- TLS-Aktivierung innerhalb des Containers; dort terminiert ein Reverse Proxy TLS.
