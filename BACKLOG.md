# Produkt-Backlog

Folgende Punkte sind bewusst nicht Teil des einfachen ersten Betriebsmodus.

## Sicherheit und Zugriff

- Optionale mTLS-Clientauthentisierung fuer den RFC-8555-Endpunkt.
- Optionaler Listener-/Netzwerkparameter fuer localhost-, konkrete Interface- oder LAN-Bindung inklusive Installationsassistent und Firewall-Hinweisen.
- Gehaerteter PKI-Modus mit Offline-Root-CA und gefuehrter Intermediate-Signierung bzw. -Rotation.
- Mehrere Administratoren, Rollen und optionale vorgeschaltete MFA/SSO-Integration.
- Manipulationsresistenter oder externer Audit-Export.
- Ueberarbeitung der TLS-Aktivierung ohne frei beschreibbare systemd-Overrides durch den Dienstbenutzer.

## Spätere Backup- und Betriebshärtung

- Regelmaessiger, gefuehrter Wiederherstellungstest mit Ergebnisprotokoll.
- Streamendes oder temp-verschluesseltes Backup, damit kein Klartext-Archiv im Temp-Verzeichnis liegt.
- Backup-Schluessel-Handling mit ausdruecklichem Export-/Rotationsprozess ausserhalb der normalen Verwaltungs-API.

## Verteilung

- Push für Geräte ohne root-Anmeldung über einen eingeschränkten sudo-Eintrag; Rotation des Push-Schlüssels.
- Ausrollen über Hersteller-APIs (Proxmox, OPNsense, Synology); dafür müsste HomeCA API-Tokens zu Zielsystemen speichern.
- Einrichtung direkt über WinRM (HTTPS-Listener oder Kerberos) und Push auf Windows-Ziele.
- Signierte Apple-Konfigurationsprofile.

## Distribution und Dokumentation

- Signierte, versionierte Releases und Checksums als bevorzugter Installationsweg; `curl | bash` nur als klar gekennzeichnete Komfortoption.
- Profile mit getesteten Produkt-/Firmware-Versionen und bekannten Einschraenkungen.
- Weitere Reverse-Proxy-Beispiele fuer HAProxy und OPNsense sowie ein Netzwerkdiagramm.
