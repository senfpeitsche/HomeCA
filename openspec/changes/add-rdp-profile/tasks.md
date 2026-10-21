# Aufgaben

## 1. Profil

- [x] `profiles.json`: Profil `rdp` mit Import, Schlüssel-ACL für `NETWORK SERVICE` (CNG- und CryptoAPI-Schlüssel), Umbinden von `RDP-Tcp` und Restore-Schritt.
- [x] Windows-Erkennung meldet `rdp`; Vorschlagsreihenfolge ergänzt.

- [x] Zertifikatsprüfung auf Port 3389 mit X.224-Aushandlung vor dem TLS-Handshake; gegen einen echten RDP-Dienst geprüft.
- [x] Assistent und Push warten so lange wie der Server (statt 100 Sekunden); TLS-Port mit Hinweis, bei RDP ohne IIS vorbelegt mit 3389.

## 2. Oberfläche und Doku

- [x] Verteilungsziele: Hinweis auf die automatische Root-CA-Installation der geführten Einrichtung (de/en).
- [x] RDP-Abschnitte in DEPLOYMENT (de/en), SSL-USAGE und TRUST-INSTALLATION korrigieren. `windows-certstore` bleibt unverändert, damit gespeicherte Profile keine Seed-Migration brauchen.

## 3. Prüfung

- [x] Tests für Profil und Erkennung; PowerShell-Parse-Test deckt das Profil ab.
- [x] Build und Testlauf.
- [x] Schlüsseldatei-Ermittlung unter Windows PowerShell 5.1 mit einem Test-PFX nachgestellt.
- [ ] Test auf einem Windows-Server mit RDP (extern, durch den Betreiber).
