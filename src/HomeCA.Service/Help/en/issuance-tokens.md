# Issuance tokens for automated clients

An issuance token is an API key for one automated client, such as a regularly reset test VM or a CI job. It can only:

- issue a key and certificate,
- sign a CSR,
- read and export as PFX the certificates it issued itself,

and only for the names in its scope. It cannot administer the CA, revoke certificates, change settings or read other certificates. If the token leaks, the damage stays inside its scope.

## Create a token

1. Open **Issuance tokens** and enter a name, for example `acceptance-vm`.
2. Enter the allowed **DNS suffixes**, one per line, for example `test.home.lan`. Allowed are `test.home.lan` itself and every subdomain such as `app.test.home.lan`; `othertest.home.lan` is not.
3. Add **IP networks** only if the client needs IP SANs. Without a network, every IP SAN is rejected.
4. Keep the **maximum validity** short. The default of 30 days is enough for tests.
5. Optionally set an **expiry date** for the token itself.
6. Copy the token immediately. It starts with `hca_it_` and is shown only once; HomeCA stores only its hash.

## Use the token

Send the token as a Bearer token to the endpoints under `/api/v1/issuance`:

```bash
curl -sS -X POST {{HOMECA_URL}}/api/v1/issuance/certificates \
  -H "Authorization: Bearer $HOMECA_ISSUANCE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"usage":"TLS","dnsNames":["app.test.home.lan"],"ipAddresses":[],"validityDays":2}'
```

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/issuance/certificates` | Issue a key and certificate |
| `POST /api/v1/issuance/certificates/csr` | Sign a CSR; its CN and all SANs must be in scope |
| `GET /api/v1/issuance/certificates/{id}` | Details of an own certificate |
| `POST /api/v1/issuance/certificates/{id}/export/pfx` | PFX export of an own certificate with a password |

Requests outside the scope return `403` with the rejected name. Certificates of other clients return `404`. An unknown, revoked or expired token returns `401`. Too many issuance requests per hour return `429`.

## Renewal and warnings

Token certificates are not renewed automatically: the client issues a new certificate when it needs one. They do not appear in expiry warnings or in **Needs attention**, because they are short-lived on purpose.

## If a token leaks

Revoke it under **Issuance tokens**. Every following request with it is rejected immediately. Certificates it issued stay valid; revoke them in **Certificates** if needed. Every use of a token is in the audit log (`issuance-token.*`).
