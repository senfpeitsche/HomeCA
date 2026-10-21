# Sicherheitsmodell und Vertrauensgrenzen

HomeCA ist eine private PKI fuer ein Homelab. Sie ersetzt keine Unternehmens-PKI, kein HSM und kein zentral verwaltetes Identity-System. Das Ziel ist, interne Dienste einfach mit nachvollziehbaren Zertifikaten zu betreiben.

## Standardmodell

HomeCA wird als einzelner, geschuetzter Dienst im LAN betrieben. Die Weboberflaeche und die API duerfen aus dem LAN erreichbar sein, muessen aber immer mit TLS betrieben werden, sobald die Ersteinrichtung abgeschlossen ist. Der Zugriff ist auf Administratoren und vertrauenswuerdige Automatisierung zu beschraenken.

Im einfachen Modus liegen Root-CA und Issuing-CA online im geschuetzten HomeCA-Datenverzeichnis. Das ist absichtlich einfach zu bedienen, hat aber eine klare Folge: Wer das HomeCA-System oder einen vollwertigen Administratorzugang uebernimmt, kann im Vertrauensbereich Zertifikate ausstellen.

## Was geschuetzt wird

- Private CA- und Zertifikatsschluessel vor normalen LAN-Clients und nicht autorisierten Benutzern.
- Verwaltung, Exporte, Connector-Geheimnisse und Backups durch die HomeCA-Anmeldung und Dateiberechtigungen des Dienstbenutzers.
- Zertifikatsvertrauen durch eine einmalig, kontrolliert verteilte Root-CA.
- Verlorene oder kompromittierte Leaf-Zertifikate durch Sperrung, CRL und Ersatzzertifikate.

## Was nicht automatisch geloest wird

- Ein kompromittierter HomeCA-Host, ein gestohlener Admin-Zugang oder ein ungeschuetztes Backup kann die gesamte PKI betreffen.
- Eine interne ACME-Ausstellungszone ist ein Vertrauensbereich. Der RFC-8555-Endpunkt akzeptiert neue Konten nur von allowlisteten Clientnetzen oder mit EAB. Eine Allowlist ersetzt jedoch keine Netzwerkzugriffskontrolle; hinter einem Reverse Proxy muss die Quell-IP-Grenze besonders bewusst gesetzt werden.
- CRLs helfen nur bei Clients und Diensten, die den Abruf und die Auswertung auch unterstuetzen.
- HomeCA sichert keine privaten Schluessel, nachdem ein Deployment-Paket auf ein Zielsystem kopiert wurde.

## Verteilungs-Tokens, Trust-Seite und ausgehende Verbindungen

- Die Einrichtung per SSH nutzt Passwort, Schlüssel und sudo-Passwort nur für die Dauer der Anfrage; sie werden weder gespeichert noch protokolliert, und Ausgaben werden um sie und das Ziel-Token bereinigt. Gespeichert wird nur der bestätigte Host-Key. Ohne bestätigten oder bei abweichendem Host-Key bricht HomeCA vor der Anmeldung ab, damit kein Angreifer im Netz Zugangsdaten oder den privaten Schlüssel abgreift. Den Fingerprint beim ersten Kontakt über die Gerätekonsole vergleichen.
- Wer die Verwaltungsoberfläche bedient, kann mit gültigen SSH-Zugangsdaten Befehle als root auf dem Gerät ausführen lassen. Jede Erkennung und Einrichtung steht mit Benutzer, Host und Host-Key im Audit-Log.
- Der Push-Schlüssel erlaubt root-Anmeldungen auf eingerichteten Geräten, aber per Forced Command nur den Start des Agenten, ohne Terminal und ohne Weiterleitung. Wer HomeCA übernimmt, kann damit Agentenläufe auslösen, aber keine Befehle ausführen. Der private Schlüssel liegt nur verschlüsselt im Datenverzeichnis und im Backup.
- Das PFX für Windows-Agenten ist mit dem Ziel-Token geschützt; wer das Token hat, hat ohnehin Zugriff auf den Schlüssel. Token, Agent und Backups liegen unter `%ProgramData%\HomeCA` mit Rechten nur für SYSTEM, Administratoren und das einrichtende Konto.
- Backups unter `/var/backups/homeca/` enthalten frühere Schlüssel und Token und sind nur für root lesbar (`700`).
- Ein Token eines Verteilungsziels kann den privaten Schlüssel dieses Ziels abrufen. Es wird nur als SHA-256-Hash gespeichert, gilt für genau ein Ziel und lässt sich rotieren; die Token-Datei auf dem Gerät ist wie der Schlüssel zu behandeln.
- `/trust` und die Trust-Anchor-Endpunkte bleiben bewusst auf einfachem HTTP. Wer im Netzpfad sitzt, kann die heruntergeladene Root austauschen; deshalb fordern Seite und Anleitungen den Fingerprint-Vergleich über einen zweiten Kanal.
- HomeCA baut ausgehende Verbindungen zu konfigurierten Webhook- und Benachrichtigungs-URLs sowie für die TLS-Prüfung zu den Hosts der Verteilungsziele auf. Diese Adressen legen nur Administratoren fest; die Nutzlasten enthalten keine Geheimnisse.
- Name Constraints auf der ausstellenden CA begrenzen den Schaden eines gestohlenen Issuing-Schlüssels auf die erlaubten Domains und Netze.

## Verbindliche Betriebsregeln

1. HomeCA nicht direkt im Internet veroeffentlichen. Zugriff nur aus dem Admin- oder Servernetz; bei Fernzugriff VPN verwenden.
2. Nach der Einrichtung HTTPS aktivieren. HTTP darf nur fuer lokale Ersteinrichtung oder bewusst isolierte Migrationen verwendet werden.
3. Root-CA-Fingerabdruck ausserhalb der Download-Verbindung pruefen, etwa in der HomeCA-Konsole, im Passwortmanager oder ueber einen zweiten Admin-Kanal. Den Fingerabdruck nicht ausschliesslich von derselben URL beziehen wie das Zertifikat.
4. Datenverzeichnis, Backup-Verzeichnis und Backup-Schluessel nur fuer `homeca` bzw. root lesbar machen. Deployment-Pakete enthalten private Schluessel.
5. DNS-API-Tokens mit kleinstmoeglichen Rechten erstellen und nach einem vermuteten Leak sofort rotieren.
6. Fuer jeden RFC-8555-Client bewusst entscheiden: sein direktes Netz allowlisten oder EAB-Zugangsdaten verwenden. EAB ist der sichere Standard fuer Clients hinter einem Reverse Proxy.

## Spaetere Haertung

Ein spaeterer geharteter Modus darf eine Offline-Root-CA vorsehen: Die Root wird nur zur Erstellung oder Rotation einer Issuing-CA entsperrt; der laufende HomeCA-Dienst verwendet ausschliesslich die Issuing-CA. Das erhoeht den Schutz, bringt aber einen manuellen, dokumentierten Rotationsprozess mit sich.
