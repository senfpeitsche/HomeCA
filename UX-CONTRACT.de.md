# HomeCA-UX-Vertrag

Der MudBlazor-Client ist ein Werkzeug für lokale Administratoren. Der
Bearer-Token existiert nur im aktiven Server-Circuit. Ein HttpOnly-Cookie für
die Browser-Sitzung enthält ausschließlich eine undurchsichtige, serverseitige
Referenz, damit eine durch den Sprachwechsel ausgelöste Neuladung die Sitzung
übersteht; **Abmelden** entfernt diese Referenz.

| Ablauf | Ergebnis | Rückmeldung |
|---|---|---|
| Anmeldung | Übersicht öffnen und geschützte Inventare laden | Eingeblendeter Fehler bei ungültigen Zugangsdaten |
| Zertifikat / CA lesen | Aktuelle Ansicht beibehalten | Eingeblendeter Leer- oder Fehlerzustand |
| CA erstellen, bearbeiten, deaktivieren oder sperren | CA-Ansicht beibehalten und Inventar neu laden | Gemeinsame Benachrichtigung; gesperrte CAs lassen sich nicht reaktivieren |
| CA löschen | Zweiten ausdrücklichen Klick verlangen; nur inaktive/gesperrte CAs ohne ausgestellte Zertifikate oder untergeordnete CAs dürfen entfernt werden | Inline-/API-Begründung plus gemeinsame Benachrichtigung |
| Zertifikat sperren | Zweiten ausdrücklichen Klick verlangen; Zertifikatsdatensatz und Export-Snapshot für Audit und Forensik behalten | Gemeinsame Benachrichtigung bestätigt CRL-Aktualisierung und den erhaltenen Datensatz |
| Ausstellung starten | Eigenen Assistenten mit Zielprofil und dessen richtlinienbasierten Vorgaben öffnen | Statushinweis nach dem lokalen Schritt |
| Connector / Backup testen | In den Einstellungen bleiben | Erfolg oder Fehler neben der Aktion anzeigen |
| Zertifikat, SSH-Zertifikat, ACME-Auftrag, Sperrung oder CRL-Aktion | Im jeweiligen Arbeitsbereich bleiben | Dauerhaftes Ergebnis oder Inline-Fehler plus gemeinsame Benachrichtigung |

Die Anwendung verwendet Englisch als Standardkultur. Die gemeinsame
Sprachauswahl speichert eine browserspezifische Wahl per ASP.NET-Core-
Request-Culture-Cookie und wirkt nach einer Neuladung; Deutsch wird im Release
vollständig unterstützt. Native `select`-Steuerelemente sind für die kleine,
feste Profilliste zulässig. Destruktive Wiederherstellungsaktionen stehen nicht
in der UI zur Verfügung; die Wiederherstellung bleibt ein dokumentierter
Betreiberablauf.
