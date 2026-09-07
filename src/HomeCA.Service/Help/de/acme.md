# ACME einrichten

HomeCA stellt ein RFC-8555-kompatibles Verzeichnis für Standard-ACME-Clients bereit.

```text
http://HOMECA:5080/acme/directory
```

Lege vor der Ausstellung eine erlaubte Zone an. Verwende `/api/v1/acme/` nicht als RFC-8555-Directory-URL.

## Einrichtungscheckliste

1. Ausstellungszone konfigurieren und ihren DNS-Connector verknüpfen.
2. Connector-Zugang und einen TXT-Record-Roundtrip testen.
3. Entscheiden, ob der Client durch die Netzwerk-Allowlist abgedeckt ist oder EAB benötigt.
4. Den ACME-Client mit der oben genannten RFC-8555-Directory-URL konfigurieren.
5. Den resultierenden Auftrag und das Zertifikat im HomeCA-Inventar prüfen.

## Client-Zugang

Client-Netze auf der Allowlist können Konten ohne External Account Binding (EAB) anlegen. Für jeden anderen Client einen individuellen EAB-Zugang in HomeCA erzeugen und die angezeigte Key-ID sowie den HMAC-Key sofort kopieren; der HMAC-Key wird nur einmal angezeigt.

In der Allowlist nur direkte Client-IP-Adressen oder CIDR-Netze eintragen. Keine Reverse-Proxy-Adresse freigeben, da dies allen dahinterliegenden Clients Zugang gewähren würde.

## DNS-01-Ausstellung

Die Ausstellungszone vor DNS-01 mit einem eingerichteten DNS-Connector verknüpfen. Zuerst den Connector und seine Berechtigung zum Anlegen von TXT-Records testen. ACME-Konto- und Auftrags-IDs bei der Fehlersuche unverändert verwenden.

Vor dem produktiven Einsatz den Connector-Check und einen TXT-Test unter **Einstellungen** ausführen. Eine erfolgreiche Verbindung allein genügt nicht: Das konfigurierte Token muss Records in der gewählten Zone anlegen und entfernen dürfen.

Einen externen ACME-Issuer nur verwenden, wenn ein öffentlich vertrauenswürdiges Zertifikat benötigt wird. Die interne HomeCA-Ausstellung für Dienste beibehalten, deren Clients der HomeCA-Root-CA bereits vertrauen.

EAB-HMAC-Keys im geschützten Secret-Store des ACME-Clients ablegen, nicht in Shell-Verlauf oder gemeinsam genutzten Konfigurationsdateien.

Nur Namen aus Zonen anfordern, die für den gewählten Issuer oder die interne Ausstellungsrichtlinie ausdrücklich aktiviert sind.

Nach einer erfolgreichen Bestellung den resultierenden Zertifikatseintrag im Inventar prüfen und ihn über denselben kontrollierten Prozess wie andere TLS-Zertifikate ausrollen.

Wenn ein Auftrag fehlschlägt, zuerst Challenge-Status und DNS-Connector-Ergebnis prüfen, bevor ein neues Konto oder ein neuer Zugang angelegt wird.

Bei DNS-01-Challenges Zeit für DNS-Propagation einplanen und nach Tests keine Diagnose-TXT-Records zurücklassen.

Bei einem Fehler das beteiligte ACME-Konto, den Auftrag und den Connector dokumentieren, damit die Analyse reproduzierbar bleibt.
