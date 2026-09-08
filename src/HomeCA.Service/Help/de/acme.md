# ACME in der Praxis

ACME automatisiert Ausstellung und Erneuerung. Nutze den **internen HomeCA-ACME-Server** für verwaltete interne Dienste wie Proxmox, Traefik oder Caddy. Nutze einen **externen ACME-Aussteller** wie Let's Encrypt nur, wenn Browser im öffentlichen Internet dem Dienst vertrauen müssen.

## Die richtige URL wählen

Standard-ACME-Clients benötigen immer das RFC-8555-Directory:

```text
{{ACME_DIRECTORY_URL}}
```

Allgemein lautet sie `https://<HomeCA-Host>:5443/acme/directory`, hinter einem Reverse Proxy meist `https://<HomeCA-Host>/acme/directory`.

> Verwende in Proxmox, Caddy, Traefik, Certbot oder acme.sh **nicht** `/api/v1/acme/...`. Diese Clients benötigen `/acme/directory`.

Wenn HomeCA HTTPS mit seiner eigenen Root-CA verwendet, muss der ACME-Client dieser Root-CA bereits vertrauen. Installiere sie zuerst unter **Vertrauen**.

## Szenario: Proxmox oder interner Dienst

1. Root- und TLS-Issuing-CA müssen aktiv sein.
2. Lege unter **Domains** eine Ausstellungszone an und aktiviere die interne Ausstellung, etwa `int.zikke.org`.
3. Konfiguriere im Client die Directory-URL und **HTTP-01**.
4. HomeCA muss den Zielnamen auf Port 80 erreichen. Der Client stellt dort bereit:

```text
http://<DNS-Name>/.well-known/acme-challenge/<Token>
```

5. Der Client installiert das Zertifikat und lädt seinen Dienst neu.

Eine Freigabe ins öffentliche Internet ist nicht nötig. Hinter einem Reverse Proxy darf `/.well-known/acme-challenge/` nicht blockiert oder umgeleitet werden.

## Clientzugang: Allowlist oder EAB

- Für einen festen internen Client trägst du seine **direkte** IP-Adresse oder sein CIDR-Netz in die ACME-Client-Allowlist ein.
- Für Proxmox, Container oder wechselnde Client-Adressen erzeugst du einen eigenen **EAB**-Zugang. Key-ID und HMAC-Key sofort in den Secret-Store des Clients kopieren; der HMAC-Key wird nur einmal angezeigt.

Die Adresse eines Reverse Proxys niemals pauschal allowlisten. Das würde allen Clients hinter dem Proxy Zugang ohne EAB geben.

Nach einem Test muss der Auftrag in HomeCA unter **ACME** den Status `valid` haben. Das ausgestellte Zertifikat erscheint unter **Zertifikate**.

## Szenario: Öffentlich vertrauenswürdige Zertifikate

Registriere unter **ACME** einen externen Aussteller, etwa mit:

```text
https://acme-v02.api.letsencrypt.org/directory
```

Verknüpfe einen DNS-Connector und teste Berechtigung sowie TXT-Roundtrip vor der ersten Bestellung. Der Connector benötigt Rechte, `_acme-challenge`-TXT-Records in der Zone anzulegen und zu entfernen.

## IP-SANs und Erneuerung

Die ACME-Richtlinie kann erlaubte IP-Netze enthalten. Bei aktivierter Funktion ergänzt HomeCA nur A-/AAAA-Adressen der validierten DNS-Namen, die in diesen CIDR-Netzen liegen. Frei angeforderte IP-Adressen werden nie übernommen.

Eine Erneuerung stellt ein neues Zertifikat aus. Der Vorgänger bleibt normalerweise bis zum Ablauf gültig. Optional kann die Richtlinie den passenden Vorgänger nach erfolgreicher Erneuerung widerrufen. Gesperrte Zertifikate bleiben für Audit und CRL in **Gesperrte Zertifikate** sichtbar.

## Fehler schnell eingrenzen

| Symptom | Zuerst prüfen |
| --- | --- |
| Konto kann nicht angelegt werden | direkte Client-IP in der Allowlist oder EAB-Key-ID/HMAC-Key |
| Auftrag bleibt `pending` | DNS-Auflösung, Port 80 und Challenge-Pfad vom HomeCA-Host |
| HTTPS zum Directory schlägt fehl | HomeCA-Root-CA im Trust Store des Clients |
| Externer Auftrag scheitert | DNS-Connector-Test, Zone und TXT-Berechtigung |

Erzeuge bei Fehlern nicht sofort ein neues Konto oder neue EAB-Zugangsdaten. Öffne zuerst den vorhandenen Auftrag in HomeCA; Account-, Order- und Challenge-Status liefern die nötigen Hinweise.
