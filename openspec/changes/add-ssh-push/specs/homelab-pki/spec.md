## ADDED Requirements

### Requirement: Sofort verteilen per SSH-Push

Das System MUSS den Agenten eines eingerichteten Ziels per SSH anstoßen können, mit einem Schlüssel, der auf dem Gerät nur diesen Agenten starten darf.

#### Scenario: Eingeschränkter Schlüssel

- **WHEN** die SSH-Einrichtung mit Push ausgeführt wird
- **THEN** MUSS die Zeile in `authorized_keys` einen Forced Command auf den Agenten und `no-pty`, `no-port-forwarding`, `no-agent-forwarding` und `no-X11-forwarding` enthalten und `authorized_keys` vorher gesichert sein

#### Scenario: Wiederherstellen

- **WHEN** `restore.sh` der Einrichtung ausgeführt wird
- **THEN** MUSS nur die HomeCA-Zeile entfernt werden und andere Schlüssel MÜSSEN erhalten bleiben

#### Scenario: Push nach Erneuerung

- **WHEN** ein Zertifikat mit Push-Ziel erneuert wird
- **THEN** MUSS HomeCA den Agenten mit dem gespeicherten Host-Key anstoßen, Ergebnis und Exit-Code speichern und bei einem Fehler benachrichtigen, ohne die Erneuerung scheitern zu lassen

#### Scenario: Host-Key geändert

- **WHEN** der Host-Key beim Push vom gespeicherten abweicht
- **THEN** MUSS der Push ohne Anmeldung abbrechen und als fehlgeschlagen gelten
