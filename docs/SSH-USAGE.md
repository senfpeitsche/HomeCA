# SSH-Zertifikate ausstellen und einsetzen

Diese Anleitung beschreibt, wie du mit HomeCA SSH-Host- und SSH-Benutzerzertifikate ausstellst, die Vertrauensstellung auf Servern und Clients einrichtest und den Alltag mit SSH-Zertifikaten organisierst.

## Hintergrund

Klassisches SSH arbeitet mit individuellen Schlüsselpaaren: Jeder Server hat einen Host-Key, den Benutzer beim ersten Verbinden manuell bestätigen (`Trust on First Use`). Jeder Benutzer hat einen persönlichen Schlüssel, den ein Administrator auf jedem Server in `authorized_keys` hinterlegen muss.

SSH-Zertifikate ersetzen dieses Modell durch eine zentrale Vertrauenshierarchie:

| Problem | Lösung mit SSH-Zertifikaten |
|---|---|
| `Are you sure you want to continue connecting?` bei jedem neuen Server | Clients vertrauen der Host-CA — signierte Host-Keys werden automatisch akzeptiert |
| `authorized_keys` auf jedem Server pflegen | Server vertrauen der User-CA — signierte Benutzerschlüssel werden automatisch akzeptiert |
| Kein Ablaufdatum für SSH-Keys | Zertifikate haben eine konfigurierbare Gültigkeitsdauer |
| Kein zentrales Inventar | HomeCA stellt Zertifikate zentral aus und protokolliert die Ausstellung |

HomeCA betreibt zwei getrennte SSH-CAs:

```
HomeCA
├─ SSH Host CA    → signiert Server-Host-Keys
└─ SSH User CA    → signiert Benutzer-Public-Keys
```

---

## Voraussetzungen

- HomeCA läuft und `/health` antwortet erfolgreich.
- Root- und Issuing-CA sind initialisiert (die SSH-CA-Schlüsselpaare werden dabei automatisch erzeugt).
- `openssh-client` ist auf dem HomeCA-Host installiert (für `ssh-keygen`).
- Auf den Zielsystemen ist OpenSSH 5.4 oder neuer vorhanden (Zertifikatunterstützung).

Alle API-Aufrufe in dieser Anleitung setzen eine gültige Sitzung voraus:

```bash
TOKEN=$(curl -s http://127.0.0.1:5080/api/v1/login \
  -H 'Content-Type: application/json' \
  -d '{"userName":"<admin>","password":"<passwort>"}' \
  | jq -r '.accessToken')
```

---

## 1. SSH-CA-Schlüssel prüfen

Nach der Initialisierung der Zertifizierungsstellen liegen die SSH-CA-Schlüssel im Datenverzeichnis:

```
<RootPath>/authorities/ssh-host/ca       # Host-CA privater Schlüssel
<RootPath>/authorities/ssh-host/ca.pub   # Host-CA öffentlicher Schlüssel
<RootPath>/authorities/ssh-user/ca       # User-CA privater Schlüssel
<RootPath>/authorities/ssh-user/ca.pub   # User-CA öffentlicher Schlüssel
```

Prüfe, ob die Schlüssel vorhanden sind:

```bash
ls -la /var/lib/homeca/authorities/ssh-host/
ls -la /var/lib/homeca/authorities/ssh-user/
```

Falls die Schlüssel fehlen (z. B. bei einem älteren Datenbestand), erzeuge sie manuell:

```bash
# Host-CA
ssh-keygen -t ed25519 -f /var/lib/homeca/authorities/ssh-host/ca -N "" -C "HomeCA SSH Host CA"
chown homeca:homeca /var/lib/homeca/authorities/ssh-host/ca*

# User-CA
ssh-keygen -t ed25519 -f /var/lib/homeca/authorities/ssh-user/ca -N "" -C "HomeCA SSH User CA"
chown homeca:homeca /var/lib/homeca/authorities/ssh-user/ca*
```

---

## 2. SSH-Zertifikat über die Weboberfläche ausstellen

Die Weboberfläche führt in einem Formular durch die Ausstellung:

1. Öffne den Bereich **Zertifikate** und wechsle zum Tab **SSH-Zertifikat**.
2. Fülle die Felder aus:

| Feld | Erklärung | Beispiel |
|---|---|---|
| **Art** | `Host` für Server-Host-Keys, `Benutzer` für persönliche Schlüssel | `Host` |
| **Identität** | Frei wählbarer Name, der im Zertifikat als Key-ID erscheint | `webserver.home.lab` |
| **Principals** | Kommagetrennte Liste der gültigen Hostnamen oder Benutzernamen | `webserver.home.lab, webserver` |
| **Öffentlicher SSH-Schlüssel** | Der Public Key, der signiert werden soll (Inhalt von `*.pub`) | `ssh-ed25519 AAAA...` |
| **Gültigkeit in Tagen** | 1 bis 3650 Tage (Standard: 365) | `365` |

3. Klicke auf **SSH-Zertifikat ausstellen**. HomeCA signiert den öffentlichen Schlüssel mit der passenden CA und zeigt das Zertifikat in einem Dialog an. Von dort kannst du den Zertifikatsinhalt in die Zwischenablage kopieren oder als `*-cert.pub`-Datei herunterladen.

Ausgestellte SSH-Zertifikate erscheinen im **SSH-Zertifikatsinventar** unterhalb des TLS-Inventars. Dort kannst du Zertifikate jederzeit erneut herunterladen oder löschen.

### Principals richtig wählen

**Host-Zertifikate:** Die Principals müssen mit den Hostnamen übereinstimmen, unter denen sich Clients zum Server verbinden. Trage alle relevanten Namen und FQDNs ein:

```
webserver, webserver.home.lab, 192.168.1.50
```

**Benutzerzertifikate:** Die Principals müssen mit den Unix-Benutzernamen übereinstimmen, als die sich der Benutzer anmelden darf:

```
root, deploy, admin
```

---

## 3. SSH-Zertifikat über die API ausstellen

### 3.1 Host-Zertifikat

Lies den öffentlichen Host-Key des Zielservers aus und übergib ihn an die API:

```bash
# Host-Key vom Zielserver holen
PUBKEY=$(ssh-keyscan -t ed25519 webserver.home.lab 2>/dev/null \
  | awk '{print $2" "$3}')

# Host-Zertifikat ausstellen
curl -s http://127.0.0.1:5080/api/v1/ssh-certificates \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{
    \"kind\": \"host\",
    \"identity\": \"webserver.home.lab\",
    \"principals\": [\"webserver.home.lab\", \"webserver\"],
    \"publicKey\": \"$PUBKEY\",
    \"validityDays\": 365
  }"
```

### 3.2 Benutzerzertifikat

```bash
# Public Key des Benutzers einlesen
PUBKEY=$(cat ~/.ssh/id_ed25519.pub)

# Benutzerzertifikat ausstellen
curl -s http://127.0.0.1:5080/api/v1/ssh-certificates \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{
    \"kind\": \"user\",
    \"identity\": \"max.mustermann\",
    \"principals\": [\"deploy\", \"admin\"],
    \"publicKey\": \"$PUBKEY\",
    \"validityDays\": 90
  }"
```

### 3.3 API-Referenz

**Endpunkt:** `POST /api/v1/ssh-certificates`

| Parameter | Pflicht | Standard | Erklärung |
|---|---|---|---|
| `kind` | Ja | — | `host` oder `user` |
| `identity` | Ja | — | Key-ID — frei wählbarer Bezeichner für das Zertifikat |
| `principals` | Ja | — | Liste gültiger Hostnamen (Host) oder Benutzernamen (User) |
| `publicKey` | Ja | — | Öffentlicher SSH-Schlüssel im OpenSSH-Format |
| `validityDays` | Nein | `365` | Gültigkeitsdauer in Tagen (1–3650) |

**Antwort:**

```json
{
  "id": "a1b2c3d4e5f6...",
  "authority": "ssh-host",
  "certificatePath": "/var/lib/homeca/certificates/ssh/a1b2c3d4e5f6...-cert.pub",
  "certificate": "ssh-ed25519-cert-v01@openssh.com AAAA..."
}
```

Das Feld `certificate` enthält den vollständigen Inhalt der signierten Zertifikatsdatei.

### 3.4 SSH-Zertifikatsinventar abrufen

```bash
curl -s http://127.0.0.1:5080/api/v1/ssh-certificates \
  -H "Authorization: Bearer $TOKEN" | jq
```

Liefert eine Liste aller ausgestellten SSH-Zertifikate mit ID, Art (host/user), Identität, Principals und Ausstellungsdatum.

### 3.5 Einzelnes SSH-Zertifikat herunterladen

```bash
curl -s http://127.0.0.1:5080/api/v1/ssh-certificates/<id>/content \
  -H "Authorization: Bearer $TOKEN" \
  -o webserver-cert.pub
```

### 3.6 SSH-Zertifikat widerrufen und KRL verteilen

```bash
curl -s -X DELETE http://127.0.0.1:5080/api/v1/ssh-certificates/<id> \
  -H "Authorization: Bearer $TOKEN"
```

Der Widerruf ergänzt die passende Key Revocation List (KRL); die Zertifikatsakte bleibt für Audit-Zwecke erhalten. Lade die KRL auf die prüfenden Systeme und binde sie ein:

```bash
# User-Zertifikate: auf dem SSH-Server
curl -fsS -H "Authorization: Bearer $TOKEN" http://HOMECA:5080/api/v1/ssh-ca-keys/user/krl \
  -o /etc/ssh/homeca-user-revoked.krl
echo 'RevokedHostKeys /etc/ssh/homeca-user-revoked.krl' | sudo tee -a /etc/ssh/sshd_config
sudo systemctl reload sshd

# Host-Zertifikate: auf den Clients
curl -fsS -H "Authorization: Bearer $TOKEN" http://HOMECA:5080/api/v1/ssh-ca-keys/host/krl \
  -o ~/.ssh/homeca-host-revoked.krl
```

Automatisiere die Aktualisierung per Konfigurationsmanagement oder Timer. Eine KRL wirkt nur dort, wo sie lokal geprüft wird.

### 3.7 SSH-CA-Public-Keys abrufen

Die öffentlichen Schlüssel der SSH-CAs können per API heruntergeladen werden — das erspart den manuellen Zugriff per SCP:

```bash
# Host-CA Public Key
curl -s http://127.0.0.1:5080/api/v1/ssh-ca-keys/host \
  -H "Authorization: Bearer $TOKEN" | jq -r '.publicKey' > ssh-host-ca.pub

# User-CA Public Key
curl -s http://127.0.0.1:5080/api/v1/ssh-ca-keys/user \
  -H "Authorization: Bearer $TOKEN" | jq -r '.publicKey' > ssh-user-ca.pub
```

---

## 4. Vertrauensstellung einrichten

Die Ausstellung allein reicht nicht — Server und Clients müssen der jeweiligen CA vertrauen. Dieser Schritt ist entscheidend.

### 4.1 CA-Public-Keys bereitstellen

Die öffentlichen CA-Schlüssel sind auf drei Wegen verfügbar:

**Weboberfläche:** Navigiere zu **Zertifizierungsstellen**. Im Bereich „SSH-Zertifizierungsstellen" findest du Download-Buttons für den Host-CA- und User-CA-Public-Key.

**API:**

```bash
curl -s http://127.0.0.1:5080/api/v1/ssh-ca-keys/host \
  -H "Authorization: Bearer $TOKEN" | jq -r '.publicKey' > ssh-host-ca.pub

curl -s http://127.0.0.1:5080/api/v1/ssh-ca-keys/user \
  -H "Authorization: Bearer $TOKEN" | jq -r '.publicKey' > ssh-user-ca.pub
```

**Direkt vom HomeCA-Host (Fallback):**

```bash
scp homeca:/var/lib/homeca/authorities/ssh-host/ca.pub ./ssh-host-ca.pub
scp homeca:/var/lib/homeca/authorities/ssh-user/ca.pub ./ssh-user-ca.pub
```

### 4.2 Host-CA auf Clients einrichten (keine Fingerprint-Warnungen mehr)

Damit SSH-Clients signierte Host-Keys automatisch akzeptieren, muss der öffentliche Schlüssel der Host-CA in die `known_hosts`-Datei eingetragen werden.

**Einzelner Benutzer:**

```bash
# Alle Hosts in der Domain home.lab vertrauen
echo "@cert-authority *.home.lab $(cat ssh-host-ca.pub)" >> ~/.ssh/known_hosts
```

**Systemweit (alle Benutzer auf einem Client):**

```bash
echo "@cert-authority *.home.lab $(cat ssh-host-ca.pub)" \
  | sudo tee -a /etc/ssh/ssh_known_hosts
```

**Muster für mehrere Domains oder IP-Bereiche:**

```bash
# Mehrere Domains
echo "@cert-authority *.home.lab,*.internal.net $(cat ssh-host-ca.pub)" >> ~/.ssh/known_hosts

# IP-Bereich
echo "@cert-authority 192.168.1.* $(cat ssh-host-ca.pub)" >> ~/.ssh/known_hosts

# Alle Hosts (nur in isolierten Lab-Netzen sinnvoll)
echo "@cert-authority * $(cat ssh-host-ca.pub)" >> ~/.ssh/known_hosts
```

Nach diesem Eintrag akzeptiert der SSH-Client Host-Zertifikate, die von der HomeCA Host-CA signiert wurden, ohne Fingerprint-Abfrage.

### 4.3 User-CA auf Servern einrichten (kein authorized_keys mehr nötig)

Damit Server signierte Benutzerzertifikate akzeptieren, muss der öffentliche Schlüssel der User-CA in der `sshd_config` hinterlegt werden.

```bash
# User-CA-Schlüssel auf den Server kopieren
sudo cp ssh-user-ca.pub /etc/ssh/trusted-user-ca.pub
sudo chmod 644 /etc/ssh/trusted-user-ca.pub
```

`/etc/ssh/sshd_config` ergänzen:

```
TrustedUserCAKeys /etc/ssh/trusted-user-ca.pub
```

SSH-Daemon neu laden:

```bash
sudo systemctl reload sshd
```

Ab sofort kann sich jeder Benutzer anmelden, dessen Schlüssel von der HomeCA User-CA signiert wurde — vorausgesetzt, der Principal im Zertifikat stimmt mit dem Unix-Benutzernamen überein.

### 4.4 Principals einschränken (optional, empfohlen)

Ohne weitere Konfiguration darf ein Benutzerzertifikat mit dem Principal `root` sich als `root` anmelden. Um dies einzuschränken, verwende eine `AuthorizedPrincipalsFile`:

```bash
# Verzeichnis anlegen
sudo mkdir -p /etc/ssh/auth_principals
```

Für jeden Benutzer eine Datei mit erlaubten Principals erstellen:

```bash
# /etc/ssh/auth_principals/deploy
# Benutzer mit Principal "deploy" oder "admin" dürfen sich als "deploy" anmelden
echo -e "deploy\nadmin" | sudo tee /etc/ssh/auth_principals/deploy

# /etc/ssh/auth_principals/root
# Nur der Principal "root-access" darf sich als root anmelden
echo "root-access" | sudo tee /etc/ssh/auth_principals/root
```

`/etc/ssh/sshd_config` ergänzen:

```
AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u
```

```bash
sudo systemctl reload sshd
```

---

## 5. Zertifikate auf Zielsystemen einspielen

### 5.1 Host-Zertifikat auf einem Server installieren

Das Host-Zertifikat muss neben dem Host-Key auf dem Server abgelegt und in der `sshd_config` referenziert werden.

Speichere das Zertifikat aus der API-Antwort (Feld `certificate`) als Datei:

```bash
# Zertifikat auf den Server kopieren
echo 'ssh-ed25519-cert-v01@openssh.com AAAA...' \
  | sudo tee /etc/ssh/ssh_host_ed25519_key-cert.pub
sudo chmod 644 /etc/ssh/ssh_host_ed25519_key-cert.pub
```

`/etc/ssh/sshd_config` ergänzen:

```
HostCertificate /etc/ssh/ssh_host_ed25519_key-cert.pub
```

```bash
sudo systemctl reload sshd
```

**Konvention:** OpenSSH erwartet die Zertifikatsdatei neben dem entsprechenden Host-Key. Für `/etc/ssh/ssh_host_ed25519_key` heißt die Zertifikatsdatei `/etc/ssh/ssh_host_ed25519_key-cert.pub`.

### 5.2 Benutzerzertifikat verwenden

Das Benutzerzertifikat muss neben dem privaten Schlüssel des Benutzers liegen:

```bash
# Zertifikat aus der API-Antwort speichern
echo 'ssh-ed25519-cert-v01@openssh.com AAAA...' > ~/.ssh/id_ed25519-cert.pub

# Verbindung testen
ssh -v deploy@webserver.home.lab
```

SSH findet das Zertifikat automatisch, wenn es im gleichen Verzeichnis wie der Schlüssel liegt und dem Namensschema `<keyname>-cert.pub` folgt.

### 5.3 Zertifikat prüfen

```bash
# Host-Zertifikat anzeigen
ssh-keygen -L -f /etc/ssh/ssh_host_ed25519_key-cert.pub

# Benutzerzertifikat anzeigen
ssh-keygen -L -f ~/.ssh/id_ed25519-cert.pub
```

Ausgabe prüfen:

```
        Type: ssh-ed25519-cert-v01@openssh.com host certificate
        Public key: ED25519-CERT SHA256:...
        Signing CA: ED25519 SHA256:... (using ssh-ed25519)
        Key ID: "webserver.home.lab"
        Serial: 0
        Valid: from 2026-08-31T12:00:00 to 2027-08-31T12:00:00
        Principals:
                webserver.home.lab
                webserver
```

Achte darauf, dass die Principals und das Ablaufdatum stimmen.

---

## 6. Skript: Host-Zertifikat in einem Schritt ausstellen und einspielen

Für die Automatisierung ein Beispielskript, das einen vorhandenen Host-Key signiert und das Zertifikat direkt einstellt:

```bash
#!/usr/bin/env bash
set -euo pipefail

HOMECA="http://127.0.0.1:5080"
SERVER="$1"
VALIDITY="${2:-365}"

# Anmelden
TOKEN=$(curl -sf "$HOMECA/api/v1/login" \
  -H 'Content-Type: application/json' \
  -d '{"userName":"admin","password":"HIER_PASSWORT"}' \
  | jq -r '.accessToken')

# Host-Key holen
PUBKEY=$(ssh-keyscan -t ed25519 "$SERVER" 2>/dev/null | awk '{print $2" "$3}')
if [ -z "$PUBKEY" ]; then echo "Kein ed25519-Key von $SERVER erhalten." >&2; exit 1; fi

# Zertifikat ausstellen
CERT=$(curl -sf "$HOMECA/api/v1/ssh-certificates" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{
    \"kind\": \"host\",
    \"identity\": \"$SERVER\",
    \"principals\": [\"$SERVER\"],
    \"publicKey\": \"$PUBKEY\",
    \"validityDays\": $VALIDITY
  }" | jq -r '.certificate')

# Zertifikat einspielen
echo "$CERT" | ssh "root@$SERVER" "
  cat > /etc/ssh/ssh_host_ed25519_key-cert.pub
  grep -q '^HostCertificate' /etc/ssh/sshd_config \
    || echo 'HostCertificate /etc/ssh/ssh_host_ed25519_key-cert.pub' >> /etc/ssh/sshd_config
  systemctl reload sshd
"

echo "Host-Zertifikat für $SERVER ausgestellt und eingespielt."
```

Aufruf:

```bash
./sign-host.sh webserver.home.lab 365
```

---

## 7. Ablauf und Erneuerung

SSH-Zertifikate haben kein automatisches Erneuerungssystem in HomeCA (anders als TLS-Zertifikate). Plane die Erneuerung manuell oder per Cron-Job:

```bash
# Cron-Job: Host-Zertifikat monatlich erneuern (auf dem HomeCA-Host)
0 3 1 * * /opt/homeca/scripts/sign-host.sh webserver.home.lab 90
```

### Empfohlene Gültigkeitsdauern

| Zertifikatstyp | Empfehlung | Begründung |
|---|---|---|
| Host-Zertifikat | 365 Tage | Server sind stabil, seltene Erneuerung ausreichend |
| Benutzerzertifikat | 1–90 Tage | Kürzere Laufzeit begrenzt das Risiko bei Schlüsselverlust |
| Benutzerzertifikat (CI/CD) | 1–7 Tage | Kurzlebig für automatisierte Deployments |

---

## 8. Fehlerbehebung

### „Initialize certificate authorities before issuing SSH certificates"

Die SSH-CA-Schlüssel fehlen im Datenverzeichnis. Prüfe Abschnitt 1 und erzeuge die Schlüssel manuell falls nötig.

### „Permission denied (publickey)" trotz Zertifikat

1. Prüfe, ob das Zertifikat neben dem Schlüssel liegt und korrekt benannt ist (`id_ed25519` erwartet `id_ed25519-cert.pub`).
2. Prüfe, ob der Principal im Zertifikat mit dem Unix-Benutzernamen übereinstimmt.
3. Prüfe die Serverseite: `TrustedUserCAKeys` muss auf den richtigen Pfad zeigen.
4. Prüfe den Verbose-Output: `ssh -vvv user@server` zeigt, ob das Zertifikat angeboten und akzeptiert wird.

### Host-Key-Warnung trotz Host-Zertifikat

1. Prüfe, ob der `@cert-authority`-Eintrag in `known_hosts` zum Hostnamen passt (Muster beachten).
2. Prüfe, ob der Server das Zertifikat ausliefert: `ssh-keyscan -c webserver.home.lab`.
3. Entferne ggf. veraltete Fingerprint-Einträge für den Host aus `~/.ssh/known_hosts`.

### Zertifikat ist abgelaufen

```bash
ssh-keygen -L -f ~/.ssh/id_ed25519-cert.pub | grep Valid
```

Stelle ein neues Zertifikat aus. Abgelaufene Zertifikate werden von OpenSSH automatisch abgelehnt.

---

## 9. Zusammenfassung

| Schritt | Host-Zertifikat | Benutzerzertifikat |
|---|---|---|
| **CA einrichten** | Automatisch bei Initialisierung | Automatisch bei Initialisierung |
| **CA-Schlüssel abrufen** | UI, API oder SCP | UI, API oder SCP |
| **Schlüssel signieren** | Weboberfläche oder API mit `kind: host` | Weboberfläche oder API mit `kind: user` |
| **Zertifikat herunterladen** | Dialog nach Ausstellung oder SSH-Inventar | Dialog nach Ausstellung oder SSH-Inventar |
| **Zertifikat einspielen** | `HostCertificate` in `sshd_config` | `*-cert.pub` neben dem privaten Schlüssel |
| **Vertrauen einrichten** | `@cert-authority` in `known_hosts` der Clients | `TrustedUserCAKeys` in `sshd_config` der Server |
| **Principals einschränken** | Im Zertifikat (Hostnamen) | `AuthorizedPrincipalsFile` auf den Servern |
| **Inventar einsehen** | Weboberfläche oder `GET /api/v1/ssh-certificates` | Weboberfläche oder `GET /api/v1/ssh-certificates` |
| **Erneuerung** | Manuell oder per Skript/Cron | Manuell oder per Skript/Cron |
