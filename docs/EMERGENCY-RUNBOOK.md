# Notfallleitfaden

Dieser Leitfaden ist fuer den normalen Homelab-Betrieb gedacht. Vor jedem Eingriff: Zeitpunkt, betroffene Namen und die getroffenen Massnahmen im Audit bzw. Betriebsjournal notieren.

## Zertifikat oder privater Server-Schluessel kompromittiert

1. Betroffenes Zertifikat im Inventar suchen und sperren.
2. CRL erzeugen und sicherstellen, dass sie unter der in Zertifikaten hinterlegten URL erreichbar ist.
3. Neues Zertifikat mit neuem Schluessel ausstellen, auf dem Ziel installieren und den Dienst neu laden.
4. Den alten Schluessel und alte Deployment-Pakete vom Zielsystem und aus Arbeitsverzeichnissen entfernen.
5. Den Dienst mit `openssl s_client` oder Browser pruefen und den Vorfall abschliessen.

Hinweis: Nicht jeder Client wertet eine CRL aus. Deshalb ist der Austausch auf dem betroffenen Dienst die wesentliche Massnahme.

## DNS-Connector-Token oder ACME-Zugang kompromittiert

1. Token beim DNS-Anbieter sofort widerrufen oder rotieren.
2. Connector in HomeCA mit dem neuen Secret aktualisieren und Berechtigungs- sowie TXT-Test ausfuehren.
3. Externe ACME-Zertifikate fuer besonders wichtige Namen erneuern.
4. Pruefen, ob unbekannte DNS-Eintraege oder ACME-Orders angelegt wurden.

## HomeCA-Host oder Administratorzugang kompromittiert

1. HomeCA vom Netzwerk trennen oder den Zugriff per Firewall/Reverse Proxy sperren. Nicht weiter als vertrauenswuerdige CA verwenden.
2. Von einem sauberen Administrationssystem aus Passwoerter und DNS-Tokens rotieren.
3. Entscheiden: Bei glaubhaftem Zugriff auf CA-Schluessel muss mindestens eine neue Issuing-CA erstellt und alle aktiven Zertifikate ersetzt werden.
4. Bei moeglichem Zugriff auf den Root-Schluessel die Root-CA als kompromittiert behandeln: neue PKI aufbauen und den neuen Root-Trust kontrolliert verteilen.
5. Ursache auf dem Host beheben und nur aus einem verifizierten Backup oder einer Neuinstallation wiederherstellen.

## HomeCA-Container oder Datenplatte verloren

1. Neuen, aktuellen Container nach der LXC-Anleitung erstellen, aber HomeCA noch nicht initialisieren.
2. Backup-Schluessel aus der getrennten, sicheren Ablage bereitstellen.
3. Backup zuerst pruefen; dann den Dienst anhalten und in ein leeres Datenverzeichnis wiederherstellen. Siehe [Betrieb](OPERATIONS.md).
4. Besitzerrechte auf `homeca:homeca` setzen, Dienst starten und `/health`, CA-Inventar, CRL und ein Testzertifikat pruefen.
5. Erst danach den Reverse Proxy bzw. den LAN-Zugriff wieder freigeben.

## Backup-Schluessel verloren

Ein HCAB1-Backup ohne den passenden 32-Byte-Schluessel ist nicht wiederherstellbar. Suche nach einer getrennt gelagerten Schluesselkopie. Gibt es keine, bleibt nur ein vorhandenes lesbares Datenverzeichnis oder der Neuaufbau der PKI samt erneuter Trust-Verteilung.

## Regelmaessiger Test

Mindestens quartalsweise einen Wiederherstellungstest in einem isolierten Test-LXC durchfuehren: Backup pruefen, wiederherstellen, CA-Fingerprint mit der Produktion vergleichen und Testzertifikat ausstellen. Das Ergebnis mit Datum notieren.

