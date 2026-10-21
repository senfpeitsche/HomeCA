# Aufgaben

## 1. Skripte

- [x] `deploy/proxmox/ct/homeca.sh`: core `build.func` gepinnt, `COMMUNITY_SCRIPTS_URL` auf den Release-Tag, `HOMECA_*` auf `var_*`, `update_script`.
- [x] `deploy/proxmox/install/homeca-install.sh`: core-Vorbereitung, Release-`homeca-install.sh` mit SHA256-Prüfung starten, `motd_ssh`, `customize`, `cleanup_lxc`.
- [x] `deploy/scripts/homeca-install.sh`: Microsoft-Repo passend zur Debian-Version.
- [x] `deploy/scripts/homeca-lxc.sh` entfernen.

## 2. Release und Doku

- [x] Release-Workflow: `ct/homeca.sh` als `homeca-lxc.sh` mit eingetragenem Tag veröffentlichen.
- [x] `docs/LXC-SETUP.md`: Ablauf, Dialoge, Variablen, Debian 13.

## 3. Prüfung

- [x] `bash -n` und ShellCheck für alle geänderten Skripte.
- [x] Release-Ersetzung des Tags lokal nachstellen.
- [ ] Test auf einem Proxmox-Host (extern, durch den Betreiber).
