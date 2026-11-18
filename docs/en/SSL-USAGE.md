# Issue and use TLS certificates

## Issue a certificate

Use **Certificates > TLS** to select a target profile, enter DNS names and
optional IP SANs, choose permitted validity and algorithm settings, then issue
the certificate. Profiles enforce their own limits and export formats.

The API provides equivalent issuance and exports. Download only through a
trusted administrator system. A deployment package may include a private key;
protect it with restrictive file permissions.

## Install on a service

Use the leaf certificate, private key and chain/fullchain format required by
the target. Typical services include nginx, HAProxy, IIS, Proxmox, OPNsense,
Home Assistant, UniFi, Synology and network appliances. Reload or restart the
service after replacing certificate material.

Verify locally with `openssl verify -CAfile chain.pem certificate.pem`, inspect
SANs with `openssl x509 -text`, and test the live service from a client that
trusts the HomeCA root CA.

## Renewal and revocation

Issuing a certificate creates a renewal plan by default: 30 days before expiry,
or after two thirds of a shorter validity. Through the API, send
`"autoRenew": false` to opt out or `"renewBeforeDays"` for a custom window
shorter than the validity; the response contains `renewalPlanId` when a plan was
created. Existing certificates get a plan in the UI or through
`/api/v1/renewal-plans`.

Each deployment package contains the profile script as `install.sh`,
`install.ps1` or `device-commands.txt` (device CLI commands to enter by hand),
depending on the target profile's script type. To get renewed certificates onto
the device automatically, add a deployment target with the agent, see
[DEPLOYMENT.md](DEPLOYMENT.md). **Renew now** in the inventory (or
`POST /api/v1/certificates/{id}/renew`) issues a successor immediately and moves
plans and deployment targets to it.

## Devices that create their own key

Appliances such as iDRAC, iLO or switches often generate the key themselves and
offer a certificate signing request. Paste it under **Issue certificate › Sign
CSR** or send it to `POST /api/v1/certificates/csr` with `csrPem`. HomeCA only
takes the public key from the CSR; names come from the CSR's SANs (or its CN)
unless `dnsNames`/`ipAddresses` are given. These certificates have no
`key.pem` and cannot be renewed by HomeCA; renew them with a new CSR.

## Issuance tokens for automated clients

Test VMs and CI jobs do not need an administrator session. Under **Issuance
tokens**, create a token with allowed DNS suffixes (for example
`test.home.lan`), optional IP networks, allowed usages and a maximum validity
(default 30 days). The token starts with `hca_it_` and is shown once. It works
only under `/api/v1/issuance`; every other endpoint rejects it with `401`.

```bash
TOKEN=hca_it_...

# Issue key and certificate (no automatic renewal)
curl -sS -X POST https://homeca.local:5443/api/v1/issuance/certificates   -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json"   -d '{"usage":"TLS","dnsNames":["app.test.home.lan"],"ipAddresses":[],"validityDays":2}'

# Sign a CSR: its CN and all SANs must be in scope
curl -sS -X POST https://homeca.local:5443/api/v1/issuance/certificates/csr   -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json"   -d "{\"csrPem\":$(jq -Rs . < request.csr),\"validityDays\":2}"

# Details and PFX export of an own certificate
curl -sS https://homeca.local:5443/api/v1/issuance/certificates/<id> -H "Authorization: Bearer $TOKEN"
curl -sS -X POST https://homeca.local:5443/api/v1/issuance/certificates/<id>/export/pfx   -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json"   -d '{"password":"pfx-password"}' -o app.pfx
```

Responses: `403` naming the rejected name for scope violations (also
`eviltest.home.lan` or `test.home.lan.attacker.tld`), `404` for certificates of
other clients, `401` for unknown, revoked or expired tokens, `429` after 30
issuance requests per hour and token (`IssuanceTokens__RequestsPerHour`).
Token certificates raise no expiry warnings. See **Help › Issuance tokens**.

Revoke a compromised certificate in HomeCA and publish the CRL. The retained
certificate record remains available for audit. See [OPERATIONS.md](OPERATIONS.md)
for backups and operational recovery.
