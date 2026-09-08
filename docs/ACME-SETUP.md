# ACME-Einrichtung

HomeCA stellt einen RFC-8555-kompatiblen internen ACME-Server bereit und kann externe ACME-Aussteller über DNS-01 verwalten. Eine vereinfachte `/api/v1/acme/`-ACME-API existiert nicht.

## Interner ACME-Server

Standard-Clients wie Proxmox, OPNsense, Caddy, Traefik, Certbot und acme.sh verwenden ausschließlich diese Directory-URL:

```text
https://<homeca-host>:5443/acme/directory
```

Hinter einem Reverse Proxy auf Port 443 lautet sie üblicherweise `https://<homeca-host>/acme/directory`. Die URL muss mit der unter **Einstellungen** konfigurierten öffentlichen HomeCA-Adresse übereinstimmen.

### Voraussetzungen

1. Root-CA und TLS-Issuing-CA sind aktiv.
2. Unter **Domains** existiert eine Zone mit aktivierter interner Ausstellung.
3. Der ACME-Client vertraut der HomeCA-Root-CA, falls die Directory-URL HTTPS verwendet.
4. Der Client kann über seine direkte IP-Allowlist oder einen eigenen EAB-Zugang ein Konto anlegen.

### HTTP-01

HomeCA unterstützt für interne RFC-8555-Orders HTTP-01. Der ACME-Client muss unter folgendem Pfad erreichbar sein:

```text
http://<DNS-Name>/.well-known/acme-challenge/<Token>
```

HomeCA ruft die Antwort ab und vergleicht die Key Authorization. Der Zielname muss vom HomeCA-Host auf Port 80 erreichbar sein; keine öffentliche Internetfreigabe ist erforderlich. Reverse Proxies dürfen diesen Pfad nicht blockieren oder umleiten.

### Zugangskontrolle

- Feste interne Clients: direkte Client-IP oder CIDR in die ACME-Client-Allowlist eintragen.
- Wechselnde Client-IP, Container oder Proxmox: eigenen EAB-Zugang erzeugen und Key-ID/HMAC-Key im Secret-Store des Clients hinterlegen.

Allowliste niemals pauschal die Adresse eines Reverse Proxys; sonst könnten alle Clients dahinter EAB umgehen.

Nach einer erfolgreichen Order steht sie unter **ACME** auf `valid`; das Zertifikat erscheint unter **Zertifikate**.

## Externe ACME-Aussteller

Nutze externe Aussteller nur für öffentlich vertrauenswürdige Zertifikate. Die Konfiguration, Ausstellerliste und externen Zertifikate liegen im getrennten Bereich **External ACME**. Registriere dort den Aussteller und verknüpfe einen DNS-Connector. Beispiele für Directory-URLs:

| Aussteller | Directory-URL |
| --- | --- |
| Let's Encrypt Produktion | `https://acme-v02.api.letsencrypt.org/directory` |
| Let's Encrypt Staging | `https://acme-staging-v02.api.letsencrypt.org/directory` |
| ZeroSSL | `https://acme.zerossl.com/v2/DV90` |
| Google Trust Services | `https://dv.acme-v02.api.pki.goog/directory` |

Vor der ersten Order Connector-Berechtigung und TXT-Roundtrip testen. Der Connector benötigt Rechte für `_acme-challenge`-TXT-Records in der jeweiligen Zone.

## IP-SANs und Erneuerung

Die ACME-Richtlinie kann aufgelöste A-/AAAA-Adressen validierter DNS-Namen als zusätzliche IP-SANs aufnehmen. Das geschieht nur innerhalb der hinterlegten CIDR-Allowlist; beliebige vom Client angeforderte IPs werden nicht übernommen.

Bei jeder Erneuerung wird ein neues Zertifikat ausgestellt. Der Vorgänger bleibt standardmäßig gültig, damit Deployments umschalten können. Optional kann die Richtlinie den passenden Vorgänger desselben ACME-Kontos mit identischen DNS-Identifiern nach erfolgreicher Erneuerung sperren. Gesperrte Zertifikate bleiben für CRL und Audit in der separaten Tabelle erhalten.

## Fehleranalyse

| Problem | Prüfen |
| --- | --- |
| Kontoerstellung scheitert | direkte Client-IP-Allowlist oder EAB-Key-ID/HMAC-Key |
| Order bleibt `pending` | DNS-Auflösung, Port 80 und Challenge-Pfad vom HomeCA-Host |
| Directory-HTTPS scheitert | Root-CA im Trust Store des Clients |
| Externe Order scheitert | DNS-Connector-Test, Zone und TXT-Berechtigung |

Bei Fehlern zuerst den vorhandenen Auftrag in HomeCA öffnen. Account-, Order- und Challenge-Status liefern die relevanten Hinweise; lege nicht vorschnell ein neues Konto oder neue EAB-Zugangsdaten an.
