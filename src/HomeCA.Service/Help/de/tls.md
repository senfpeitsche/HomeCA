# TLS-Zertifikate ausstellen

Wähle unter **Zertifikate** ein TLS-Profil, trage die DNS-Namen ein und stelle das Zertifikat aus. Installiere Zertifikat und Kette auf dem Zielsystem.

> Die Root-CA muss auf den Clients installiert sein, die sich mit dem Dienst verbinden.

## Prüfen

```sh
openssl s_client -connect service.example:443 -showcerts
```

| Element | Empfehlung |
| --- | --- |
| Schlüssel | ECC P-256 |
| Erneuerung | Vor Ablauf einrichten |

## Deployment-Paket

Das ZIP-Deployment-Paket enthält Zertifikat, privaten Schlüssel, Kette, Prüfsummen, Installationshinweise und den Snapshot des Erneuerungsskripts. Behandle es wie den privaten Schlüssel: nur über einen geschützten Kanal übertragen und nach der Installation entfernen.

Nutze die einzelnen Exporte, wenn das Zielsystem getrennte Dateien erwartet:

| Export | Typischer Einsatz |
| --- | --- |
| PEM | Linux-Webdienste |
| Key | Privater Schlüssel neben PEM |
| Chain / Fullchain | Dienste mit separater Ausstellerkette |
| PFX | Windows- und Java-Keystores |

## Erneuerung und Sperrung

Lege nach der erfolgreichen Installation einen Erneuerungsplan an. HomeCA erneuert aktive Pläne mit den ursprünglichen Zertifikatseinstellungen. Bei einem Verdacht auf kompromittierte Schlüssel das Zertifikat im Inventar sperren und einen Ersatz ausrollen; anschließend wird die CRL aktualisiert.

Für automatisierte Prüfungen mit `GET /api/v1/warnings/expiring` Zertifikate mit bevorstehendem Ablauf kontrollieren. Zertifikats-CRLs werden über den im Zertifikat konfigurierten CRL-Verteilungspunkt veröffentlicht; diesen Endpunkt nicht durch einen übersetzten Wert ersetzen.

Beim Einspielen eines Serverzertifikats die vom Dienst benötigte Ausstellerkette mit ausliefern. Nach Neustart oder Reload des Dienstes von einem getrennten Client testen, damit ein zwischengespeichertes Zertifikat keine unvollständige Kette verdeckt.

Den Zugriff auf Dateien mit privaten Schlüsseln auf das Dienstkonto beschränken und Backups verschlüsselt aufbewahren.

Die Seriennummer des ausgerollten Zertifikats dokumentieren, damit es bei einem Vorfall schnell im Inventar gefunden wird.

## Ausstellungsparameter

`TLS-Server` für Serverzertifikate und `mTLS / Client` für Client-Authentifizierung verwenden. DNS-Namen durch Kommas trennen; Wildcards wie `*.home.lab` werden unterstützt. IP-SANs hängen vom gewählten Zielprofil ab. Die Laufzeit ist durch das Profil begrenzt; einige Profile verlangen RSA statt ECC.

| Zielprofil | Standardschlüssel | Exporte | Hinweis |
| --- | --- | --- | --- |
| Generisches TLS | ECC | PEM, PFX | Universelles Profil |
| Windows IIS | RSA | PFX | IIS-Bindung und RDP |
| Proxmox VE | ECC | PEM | Kein IP-SAN |
| OPNsense | RSA | PFX, PEM | Firewall-Weboberfläche |
| Home Assistant | ECC | PEM | Kein IP-SAN |
| UniFi OS | ECC | PEM | Kein IP-SAN |
| HAProxy | ECC | PEM | Bundle-Export |
| nginx | ECC | PEM | Fullchain für `ssl_certificate` |
| Cisco Switch | RSA | PEM, PFX | PKCS12-Import per CLI |
| Synology DSM | RSA | PEM | Kein IP-SAN |

`Chain` enthält Issuing- und Root-CA; `Fullchain` kombiniert Zertifikat und Kette; `Bundle` kombiniert Schlüssel, Zertifikat und Kette für Dienste wie HAProxy.

Bei API-Ausstellung Feldnamen wie `dnsNames`, `ipAddresses`, `validityDays`, `keyAlgorithm`, `rsaKeySize` und `targetProfileId` exakt wie dokumentiert verwenden.

Das ZIP-Paket ist keine zusätzliche Verschlüsselungsgrenze; es enthält Material mit privaten Schlüsseln und muss entsprechend behandelt werden.

Wenn ein Zielprofil RSA verlangt, diese Vorgabe nicht mit ECC überschreiben; das Profil enthält die Kompatibilitätsanforderungen des Ziels.

Ein erneuertes Zertifikat auf dem Zielservice prüfen, bevor das bisherige Deployment-Artefakt außer Betrieb genommen wird.

Sicherstellen, dass Clients der Root-CA vertrauen, bevor ein TLS-Deployment als Serverzertifikatsfehler diagnostiziert wird.

Ablaufwarnungen regelmäßig prüfen und sie über Erneuerungspläne bearbeiten, bevor Zertifikate ihre betriebliche Frist erreichen.

Das bisherige funktionierende Deployment behalten, bis das Ersatzzertifikat unabhängig geprüft wurde.

Die mit dem gewählten Zielprofil ausgelieferten Installationshinweise verwenden; sie enthalten Format- und dienstspezifische Deployment-Anforderungen.
