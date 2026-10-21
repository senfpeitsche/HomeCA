# RDP-Profil mit automatischem Umbinden

## Warum

Windows-Server werden meist per RDP verwaltet. Ohne eigenes Zertifikat zeigt jeder Client eine Warnung zum selbstsignierten Zertifikat. Mit `windows-certstore` liegt das HomeCA-Zertifikat zwar im Speicher, der RDP-Listener bleibt aber auf dem alten Thumbprint. Nach jeder Erneuerung muss jemand von Hand umbinden, sonst fällt RDP nach Ablauf auf das selbstsignierte Zertifikat zurück.

## Was ändert sich

- **Profil `rdp`:** importiert das PFX nach `LocalMachine\My`, gibt `NETWORK SERVICE` Leserecht auf den privaten Schlüssel und setzt `SSLCertificateSHA1Hash` des Listeners `RDP-Tcp` auf den neuen Thumbprint. Den vorherigen Thumbprint hält `restore.ps1` fest. Bestehende Sitzungen bleiben offen.
- **Prüfung:** Auf Port 3389 handelt HomeCA vor dem TLS-Handshake RDP aus (X.224), damit die Prüfung des ausgelieferten Zertifikats auch für RDP funktioniert.
- **Erkennung:** Die Windows-Erkennung meldet `rdp`, wenn Remotedesktop-Verbindungen erlaubt sind. Die geführte Einrichtung schlägt das Profil dann vor.
- **Verteilungsziele:** Die Seite nennt, dass die geführte Einrichtung die Root-CA standardmäßig im Vertrauensspeicher des Geräts ablegt.
- **Doku:** Die RDP-Anleitungen nutzen `Set-CimInstance` statt der nicht vorhandenen Methode `SetSSLCertificateSHA1Hash`. `windows-certstore` bleibt unverändert.

## Nicht im Umfang

- RD-Gateway, RD-Web und Verbindungsbroker (eigene Zertifikatsrollen im Server-Manager).
- Root-CA-Installation über die manuellen Einrichtungsbefehle.
