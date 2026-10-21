# Einfacheres Onboarding für Zertifikate (Quick Wins)

## Warum

HomeCA soll den Umgang mit Zertifikaten so einfach wie möglich machen. Drei Stellen erzeugten unnötige Reibung:

- Das Deployment-Paket enthielt immer `install.ps1`, auch wenn das Profil Bash-Befehle (Proxmox, nginx, HAProxy) oder Gerätebefehle (Cisco, Huawei) enthielt.
- Erneuerungspläne waren ein separater, leicht vergessener Schritt; ohne Plan läuft ein Zertifikat still ab.
- Name, Zweck und Anleitung der Zielprofile waren nur deutsch, obwohl die Oberfläche standardmäßig englisch ist.

## Was ändert sich

- Zielprofile erhalten einen Skripttyp (`sh`, `ps1`, `cli`). Das Paket enthält passend `install.sh`, `install.ps1` oder `device-commands.txt`; ohne Angabe wird PowerShell anhand typischer Cmdlets erkannt, sonst Shell.
- `POST /api/v1/certificates` legt standardmäßig einen Erneuerungsplan an (`autoRenew`, optional `renewBeforeDays`). Die Oberfläche bietet die Option vorausgewählt an.
- Zielprofile tragen englische Basistexte und optionale Übersetzungen (`localizations`). Unveränderte Standardprofile älterer Installationen werden beim Lesen auf den neuen Stand migriert; bearbeitete Profile bleiben unangetastet.

## Nicht im Umfang

- Automatisches Ausrollen erneuerter Zertifikate auf Zielsysteme.
- Übersetzung der Skripte selbst; Skripte und Paket-README bleiben englisch.
