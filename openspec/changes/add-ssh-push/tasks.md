# Aufgaben

## 1. Schlüssel

- [x] ECDSA-P-256-Push-Schlüssel einmalig erzeugen, privat verschlüsselt speichern, öffentlich im OpenSSH-Format bereitstellen.
- [x] SSH-Verbindung mit Push-Schlüssel und gespeichertem Host-Key.

## 2. Einrichtung

- [x] Option „Sofort verteilen per Push“ in Anfrage und Assistent.
- [x] Einrichtungsskript prüft `PermitRootLogin`, sichert `authorized_keys`, ersetzt die eigene Zeile mit Forced Command und Einschränkungen.
- [x] `restore.sh` entfernt nur die HomeCA-Zeile.
- [x] Ergebnis (Push aktiv oder nicht verfügbar) am Ziel speichern.

## 3. Agent

- [x] Sperrverzeichnis gegen parallele Läufe.

## 4. Push

- [x] Push-Dienst: Agent anstoßen, Ergebnis speichern, Ausgabe begrenzen.
- [x] Nach jeder Erneuerung für umgehängte Ziele mit Push auslösen; Fehler benachrichtigen.
- [x] `POST /api/v1/deployment-targets/{id}/push` und Push am Ziel abschalten.
- [x] Neues Benachrichtigungsereignis „Push fehlgeschlagen“.

## 5. Oberfläche

- [x] Checkbox im Assistenten, Status und „Jetzt verteilen“ in der Zielliste.

## 6. Qualität

- [x] Tests für Schlüssel, Skript, Push-Dienst und Erneuerung.
- [x] Ressourcen, Doku und Bedrohungsmodell auf Deutsch und Englisch.
- [x] Ende-zu-Ende-Prüfung gegen einen echten SSH-Server.
