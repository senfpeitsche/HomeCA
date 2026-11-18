# Ausstellungs-Tokens für automatische Clients

Ein Ausstellungs-Token ist ein API-Schlüssel für genau einen automatischen Client, etwa eine regelmäßig zurückgesetzte Test-VM oder einen CI-Job. Er kann nur:

- einen Schlüssel mit Zertifikat ausstellen,
- einen CSR signieren,
- die selbst ausgestellten Zertifikate lesen und als PFX exportieren,

und das nur für die Namen in seinem Geltungsbereich. Er kann die CA nicht verwalten, keine Zertifikate widerrufen, keine Einstellungen ändern und keine fremden Zertifikate lesen. Gelangt der Token in falsche Hände, bleibt der Schaden auf seinen Geltungsbereich begrenzt.

## Token erstellen

1. Öffnen Sie **Ausstellungs-Tokens** und geben Sie einen Namen ein, zum Beispiel `abnahme-vm`.
2. Tragen Sie die erlaubten **DNS-Suffixe** ein, eines pro Zeile, zum Beispiel `test.home.lan`. Erlaubt sind `test.home.lan` selbst und jede Subdomain wie `app.test.home.lan`, nicht aber `anderestest.home.lan`.
3. Tragen Sie **IP-Netze** nur ein, wenn der Client IP-SANs braucht. Ohne Netz wird jeder IP-SAN abgelehnt.
4. Halten Sie die **maximale Gültigkeit** kurz. Die Vorgabe von 30 Tagen reicht für Tests.
5. Setzen Sie bei Bedarf ein **Ablaufdatum** für den Token selbst.
6. Kopieren Sie den Token sofort. Er beginnt mit `hca_it_` und wird nur einmal angezeigt; HomeCA speichert nur seinen Hash.

## Token verwenden

Senden Sie den Token als Bearer-Token an die Endpunkte unter `/api/v1/issuance`:

```bash
curl -sS -X POST {{HOMECA_URL}}/api/v1/issuance/certificates \
  -H "Authorization: Bearer $HOMECA_ISSUANCE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"usage":"TLS","dnsNames":["app.test.home.lan"],"ipAddresses":[],"validityDays":2}'
```

| Endpunkt | Zweck |
| --- | --- |
| `POST /api/v1/issuance/certificates` | Schlüssel und Zertifikat ausstellen |
| `POST /api/v1/issuance/certificates/csr` | CSR signieren; CN und alle SANs müssen im Geltungsbereich liegen |
| `GET /api/v1/issuance/certificates/{id}` | Details eines eigenen Zertifikats |
| `POST /api/v1/issuance/certificates/{id}/export/pfx` | PFX-Export eines eigenen Zertifikats mit Passwort |

Anfragen außerhalb des Geltungsbereichs liefern `403` mit dem abgelehnten Namen. Zertifikate anderer Clients liefern `404`. Ein unbekannter, widerrufener oder abgelaufener Token liefert `401`. Zu viele Ausstellungen pro Stunde liefern `429`.

## Erneuerung und Warnungen

Token-Zertifikate werden nicht automatisch erneuert: Der Client stellt bei Bedarf ein neues Zertifikat aus. Sie erscheinen weder in Ablaufwarnungen noch unter **Braucht Aufmerksamkeit**, weil sie absichtlich kurzlebig sind.

## Wenn ein Token bekannt wird

Widerrufen Sie ihn unter **Ausstellungs-Tokens**. Jede folgende Anfrage mit ihm wird sofort abgelehnt. Bereits ausgestellte Zertifikate bleiben gültig; widerrufen Sie sie bei Bedarf unter **Zertifikate**. Jede Nutzung eines Tokens steht im Audit-Log (`issuance-token.*`).
