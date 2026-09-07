# SSH-Zertifikate ausstellen

Wähle ein SSH-Profil, hinterlege öffentlichen Schlüssel und Principals und verteile das ausgestellte Zertifikat zusammen mit dem öffentlichen Schlüssel.

| Feld | Host-Zertifikat | Benutzerzertifikat |
| --- | --- | --- |
| Identität | Server- oder Dienstname | Personen- oder Automatisierungsidentität |
| Principals | Akzeptierte Hostnamen | Akzeptierte Login-Namen |
| Öffentlicher Schlüssel | Host Public Key | User Public Key |
| Vertrauen beim Prüfer | `known_hosts` | `TrustedUserCAKeys` |

```sh
ssh -i ~/.ssh/id_ed25519 user@host
```

Hinterlege den öffentlichen SSH-CA-Schlüssel auf dem Server über `TrustedUserCAKeys` oder `TrustedHostCAKeys`.

## Ausstellen und einspielen

Das Zertifikat erst nach Prüfung des Public-Key-Formats und der vorgesehenen Principals ausstellen. Das Host-Zertifikat neben dem Host-Key ablegen und `HostCertificate` in der `sshd_config` konfigurieren. Ein Benutzerzertifikat neben seinem privaten Schlüssel ablegen; OpenSSH erkennt es dann über die Standard-Namenskonvention.

## Vertrauensstellung einrichten

HomeCA verwendet getrennte CAs für Host- und Benutzerzertifikate. Trage die Host-CA in die `known_hosts`-Dateien der Clients ein und konfiguriere die User-CA mit `TrustedUserCAKeys` in der `sshd_config` auf allen akzeptierenden Servern.

```sh
echo "@cert-authority *.home.lab ssh-ed25519 AAAA..." >> ~/.ssh/known_hosts
sudo systemctl reload sshd
```

Mit `ssh-keygen -L -f certificate.pub` lässt sich ein ausgestelltes Zertifikat prüfen. Bei Verlust oder Kompromittierungsverdacht eines privaten Schlüssels Zertifikat ersetzen und sperren.

Vor Ablauf des aktuellen Zertifikats einen Ersatz ausstellen, neben dem zugehörigen Schlüssel installieren und zunächst eine Verbindung testen, bevor das bisherige Zertifikat entfernt wird. Den öffentlichen CA-Schlüssel bei einer regulären Zertifikatserneuerung unverändert lassen.

Vor dem Einspielen das Gültigkeitsintervall in der Ausgabe von `ssh-keygen -L` prüfen.

Sicherstellen, dass jeder konfigurierte Principal zur vorgesehenen Host- oder Benutzeridentität passt.

Host- und User-CA-Vertrauen getrennt behandeln; die Änderung der einen konfiguriert nicht das Vertrauen für die andere.

Prüfen, dass das Zertifikat neben genau dem öffentlichen/privaten Schlüsselpaar installiert wird, für das es ausgestellt wurde.

Die Syntax der `sshd_config` vor dem Reload von `sshd` prüfen, damit der Remote-Zugang nicht unterbrochen wird.

Nach dem Austausch eines Host-Zertifikats von einem Client mit Vertrauen in die Host-CA erneut verbinden und prüfen, dass keine Host-Key-Abfrage erscheint.

Bei Benutzerzertifikaten prüfen, dass der Zielserver den vorgesehenen Principal erkennt, bevor bestehende Zugangsmethoden entfernt werden.

Ausstellungsprofil und konfigurierte Laufzeitpolitik mit der Zugriffsrichtlinie des Zielsystems abgestimmt halten.
