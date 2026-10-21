## ADDED Requirements

### Requirement: LXC-Einrichtung auf Proxmox VE

Das System MUSS ein Release-Skript `homeca-lxc.sh` bereitstellen, das auf einem Proxmox-VE-Host einen Debian-13-Container über community-scripts/core erstellt und darin die gleichnamige Release-Version von HomeCA installiert.

#### Scenario: Host ohne local-lvm

- **WHEN** der Host nur ZFS- oder andere Storages anbietet
- **THEN** MUSS das Skript einen vorhandenen Storage für Container-Disk und Template anbieten, statt mit einem festen Storage-Namen abzubrechen

#### Scenario: Neue Template-Version

- **WHEN** Proxmox eine ältere Debian-Template-Version aus dem Index entfernt hat
- **THEN** MUSS das Skript die aktuelle Version des Templates verwenden

#### Scenario: Versionstreue

- **WHEN** `homeca-lxc.sh` aus dem Release `vX.Y.Z` stammt
- **THEN** MÜSSEN das Container-Installationsskript und das installierte HomeCA aus `vX.Y.Z` stammen, sofern `HOMECA_VERSION` nichts anderes vorgibt

#### Scenario: Installation im Container

- **WHEN** core den Container erstellt hat
- **THEN** MUSS HomeCA mit Dienstbenutzer `homeca`, sudoers-Eintrag für die TLS-Aktivierung und SHA256-geprüftem Release-Bundle installiert werden
