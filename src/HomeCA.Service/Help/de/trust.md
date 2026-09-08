# Vertrauensanker installieren

Installiere die HomeCA-Root-CA in jedem Client-Vertrauensspeicher. Für Unix-artige Systeme nutze den PEM-, für Windows den CER-Endpunkt.

Bei einem HTTPS-Server, dessen Zertifikat von genau dieser Root-CA stammt,
ist der erste Download ein Bootstrap-Schritt: Prüfe den Fingerabdruck der
Root-CA vorher über einen unabhängigen administrativen Kanal und nutze
`--insecure` nur für diesen ersten Abruf.

```sh
curl --fail --show-error --location --insecure -o homeca-root-ca.pem https://HOMECA:5443/api/v1/trust-anchor/pem
```

> Die Root-CA gehört in den Vertrauensspeicher. Intermediate-CAs werden mit der TLS-Zertifikatskette ausgeliefert.

## Hinweise je Plattform

Unter Debian oder Ubuntu die PEM-Datei nach `/usr/local/share/ca-certificates/` kopieren und `update-ca-certificates` ausführen. Auf Proxmox-Knoten erfolgt das üblicherweise als `root`, daher ohne `sudo`. Unter Windows die CER-Datei in **Lokaler Computer → Vertrauenswürdige Stammzertifizierungsstellen** importieren. Für verwaltete Windows-Geräte die Root-CA über Gruppenrichtlinien verteilen.

Auch Windows benötigt bei einer HTTPS-Instanz einen einmaligen Bootstrap-Abruf, weil das Server-Zertifikat vor der Root-CA-Installation noch nicht validiert werden kann. Führe dazu die Kopier-Anleitung als Administrator aus; sie verwendet die Zertifikatsausnahme nur für den ersten Download und prüft den Abruf danach ohne Ausnahme.

| Plattform | Aktion für den Vertrauensanker |
| --- | --- |
| Debian / Ubuntu | PEM unter `/usr/local/share/ca-certificates/` installieren, dann `update-ca-certificates` ausführen |
| Windows | CER in den Root-Speicher des lokalen Computers importieren |
| Active Directory | Root-CA über Gruppenrichtlinien verteilen |
| macOS | Root-CA in den System-Schlüsselbund importieren und als vertrauenswürdig markieren |
| Firefox | Enterprise-Richtlinie verwenden oder in den verwalteten Vertrauensspeicher importieren |

Für SSH wird Vertrauen separat eingerichtet: Die SSH-Host-CA über `known_hosts` verteilen und `TrustedUserCAKeys` auf Servern konfigurieren, die Benutzerzertifikate akzeptieren.

## Prüfen

Nach der Installation der Root-CA erneut eine Verbindung zu einem internen TLS-Dienst aufbauen und prüfen, dass der Client seine Zertifikatskette ohne Warnung vor einem nicht vertrauenswürdigen Aussteller akzeptiert.

Vertrauensanker nur von der erwarteten HomeCA-Adresse herunterladen und Subject oder Fingerabdruck vor einer breiten Verteilung über einen unabhängigen administrativen Kanal prüfen.

Eine Intermediate-CA nicht in den Root-Vertrauensspeicher importieren, außer ein Zielprodukt verlangt diese Ausnahme ausdrücklich.

Die Root-CA nur an verwaltete Clients und Dienste verteilen, die HomeCA-ausgestellte Zertifikate prüfen müssen.

Nach der Verteilung per Gruppenrichtlinie an einem repräsentativen Client prüfen, dass die Root-CA angekommen ist, bevor Dienste darauf umgestellt werden.

Wenn eine Vertrauensverteilung unerwartetes Client-Verhalten verursacht, den Rollout anhalten und den neu verteilten Anker erst nach Prüfung des Zielbereichs aus der betroffenen Richtlinie entfernen.

Einen repräsentativen internen HTTPS-Dienst verwenden, um zu prüfen, dass die installierte Root-CA die vollständige Kette validiert.

Ein Inventar der Systeme führen, die die Root-CA erhalten haben, damit ein späterer Ersatz oder eine Entfernung sicher eingegrenzt werden kann.
