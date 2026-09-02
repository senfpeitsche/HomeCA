# Zertifikats-Lebenszyklus

Diese kurze Routine verhindert, dass Zertifikate zu anonymen Dateien ohne Verantwortlichkeit werden.

## Neuer Dienst

1. DNS-Namen, Zielsystem und verantwortliche Person festlegen.
2. Passendes Zielprofil waehlen und entscheiden, ob TLS oder mTLS benoetigt wird.
3. Fuer die Ausstellungszone **alles erlaubt** oder eine Allowlist waehlen. Fuer einzelne administrative Dienste ist die Allowlist vorzuziehen.
4. Zertifikat mit moeglichst kurzer, fuer den Dienst praktischer Laufzeit ausstellen.
5. Deployment-Paket nur auf einem vertrauenswuerdigen Admin-System herunterladen, private Schluessel mit restriktiven Rechten installieren und Dienst neu laden.
6. Aus einem Clientnetz die Kette, den Namen und die Laufzeit pruefen. Im Inventar Ziel und Eigentumer festhalten.

## Regelbetrieb und Erneuerung

- Pro Dienst einen Renewal-Plan oder den nativen ACME-Client verwenden; nach einer Erneuerung muss der Zielservice seine Dateien auch neu laden.
- Ablaufwarnungen taeglich in Monitoring oder E-Mail pruefen.
- Monatlich Inventar pruefen: unbekannte, nicht genutzte oder bald ablaufende Zertifikate klaeren.
- Nach einem Profil-, DNS- oder Dienstwechsel eine Testerneuerung ausfuehren.

## Dienst wird geaendert oder stillgelegt

1. Neue Namen zuerst per SAN zum bestehenden oder neuen Zertifikat hinzufuegen.
2. Nach erfolgreichem Umzug alte Namen entfernen und das alte Zertifikat sperren, falls der Schluessel oder das System nicht mehr vertrauenswuerdig ist.
3. Abgeschaltete Dienste aus Erneuerungsplaenen, ACME-Konfiguration, DNS-Allowlist und Inventar entfernen.
4. Alte private Schluessel und Deployment-Pakete sicher loeschen.

## CA-Rotation

Eine ablaufende Issuing-CA rechtzeitig durch eine neue Intermediate unter derselben Root ersetzen. Die alte Intermediate bleibt verfuegbar, bis alle von ihr ausgestellten Zertifikate erneuert oder abgelaufen sind. Details stehen in [Betrieb](OPERATIONS.md).

Die Root-CA nur bei Ablauf, glaubhafter Kompromittierung oder geplantem Wechsel rotieren. Das ist ein eigenes Projekt: neue Root verteilen, alle Issuing-CAs und Leaf-Zertifikate ersetzen und alte Vertrauensanker erst danach entfernen.

