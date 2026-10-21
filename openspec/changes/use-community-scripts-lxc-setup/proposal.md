# LXC-Einrichtung über community-scripts/core

## Warum

`homeca-lxc.sh` erstellt den Container mit eigenen Annahmen: fester Template-Name, Storage `local-lvm`, nur DHCP oder eine IP. Auf Hosts mit ZFS, anderem Storage-Namen oder neuer Template-Version bricht das Setup ab (`no such template`, `storage 'local-lvm' does not exist`). Die Proxmox VE Helper-Scripts lösen genau diese Fragen bereits: Storage- und Template-Auswahl, Netzwerk, VLAN, SSH-Schlüssel, erweiterte Einstellungen per Dialog.

PR #2 bereitet HomeCA für community-scripts vor. Davon übernehmen wir nur die Container-Erstellung; der Installer im Container bleibt unserer.

## Was ändert sich

- **Container über core:** `deploy/proxmox/ct/homeca.sh` lädt `build.func` aus `community-scripts/core` (auf einen Commit gepinnt, per `COMMUNITY_SCRIPTS_CORE_URL` überschreibbar). Core erstellt den Container mit Standard- oder erweiterten Einstellungen.
- **Installer im Container:** core holt `install/homeca-install.sh` aus unserem Repo, festgelegt auf den Release-Tag. Dieses Skript bereitet den Container mit den core-Funktionen vor und startet danach den bekannten `homeca-install.sh` aus dem Release. Dienstbenutzer `homeca`, sudoers für TLS, Release-Bundle mit SHA256-Prüfung und Helfer-Skripte bleiben unverändert.
- **Debian 13:** Der Container nutzt Debian 13. `homeca-install.sh` bindet das Microsoft-Paket-Repo passend zur Debian-Version aus `/etc/os-release` ein.
- **Release-Asset bleibt `homeca-lxc.sh`:** Der Release-Workflow kopiert `ct/homeca.sh` als `homeca-lxc.sh` und trägt den Tag als Standardversion ein. Der bekannte Einzeiler funktioniert weiter.
- **Alte Variablen:** `HOMECA_CTID`, `HOMECA_HOSTNAME`, `HOMECA_DISK`, `HOMECA_RAM`, `HOMECA_CORES`, `HOMECA_STORAGE`, `HOMECA_BRIDGE` und `HOMECA_NET` werden auf die `var_*`-Einstellungen von core abgebildet.
- **Update:** `update` im Container ruft `/opt/homeca/homeca-update.sh` auf.

## Nicht im Umfang

- Einreichung bei community-scripts (bleibt in PR #2).
- Betrieb als root, `setup_dotnet` oder `homeca-linux-x64.tar.gz` statt Bundle.
- Migration bestehender Debian-12-Container.
