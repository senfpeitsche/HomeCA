## ADDED Requirements

### Requirement: RDP-Zertifikat auf Windows-Servern

Das System MUSS ein Profil `rdp` bereitstellen, das ein ausgestelltes Zertifikat für den Remotedesktop-Listener `RDP-Tcp` installiert und bei jeder Erneuerung umbindet.

#### Scenario: Erneuerung

- **WHEN** der Agent ein erneuertes Zertifikat mit dem Profil `rdp` installiert
- **THEN** MUSS `SSLCertificateSHA1Hash` von `RDP-Tcp` auf den neuen Thumbprint zeigen, `NETWORK SERVICE` den privaten Schlüssel lesen dürfen und `restore.ps1` den vorherigen Thumbprint wiederherstellen können

#### Scenario: Erkennung

- **WHEN** die geführte Einrichtung ein Windows-Gerät erkennt, das Remotedesktop-Verbindungen erlaubt
- **THEN** MUSS sie das Profil `rdp` vorschlagen
