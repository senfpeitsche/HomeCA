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
- Eine interne ACME-Ausstellungszone ist ein Vertrauensbereich. In der Einstellung **alles erlaubt** kann jeder Client, der den ACME-Endpunkt erreichen darf, Zertifikate fuer diese Zone erhalten. Eine Allowlist begrenzt dies auf die hinterlegten Namen bzw. Muster, ersetzt aber keine Netzwerkzugriffskontrolle.
- CRLs helfen nur bei Clients und Diensten, die den Abruf und die Auswertung auch unterstuetzen.
- HomeCA sichert keine privaten Schluessel, nachdem ein Deployment-Paket auf ein Zielsystem kopiert wurde.

## Verbindliche Betriebsregeln

1. HomeCA nicht direkt im Internet veroeffentlichen. Zugriff nur aus dem Admin- oder Servernetz; bei Fernzugriff VPN verwenden.
2. Nach der Einrichtung HTTPS aktivieren. HTTP darf nur fuer lokale Ersteinrichtung oder bewusst isolierte Migrationen verwendet werden.
3. Root-CA-Fingerabdruck ausserhalb der Download-Verbindung pruefen, etwa in der HomeCA-Konsole, im Passwortmanager oder ueber einen zweiten Admin-Kanal. Den Fingerabdruck nicht ausschliesslich von derselben URL beziehen wie das Zertifikat.
4. Datenverzeichnis, Backup-Verzeichnis und Backup-Schluessel nur fuer `homeca` bzw. root lesbar machen. Deployment-Pakete enthalten private Schluessel.
5. DNS-API-Tokens mit kleinstmoeglichen Rechten erstellen und nach einem vermuteten Leak sofort rotieren.
6. Fuer jede interne Ausstellungszone bewusst entscheiden: **alles erlaubt** fuer ein vollstaendig vertrauenswuerdiges Netz oder **Allowlist** fuer feste, bekannte Dienste.

## Spaetere Haertung

Ein spaeterer geharteter Modus darf eine Offline-Root-CA vorsehen: Die Root wird nur zur Erstellung oder Rotation einer Issuing-CA entsperrt; der laufende HomeCA-Dienst verwendet ausschliesslich die Issuing-CA. Das erhoeht den Schutz, bringt aber einen manuellen, dokumentierten Rotationsprozess mit sich.

