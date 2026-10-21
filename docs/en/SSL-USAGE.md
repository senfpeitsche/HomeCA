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

Revoke a compromised certificate in HomeCA and publish the CRL. The retained
certificate record remains available for audit. See [OPERATIONS.md](OPERATIONS.md)
for backups and operational recovery.
