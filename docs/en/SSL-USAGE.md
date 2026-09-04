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

Create a renewal plan in the UI or through the API. After renewal, deploy the
new export and reload the target service; HomeCA cannot reload arbitrary
targets automatically. Monitor expiring certificates through the warnings API.

Revoke a compromised certificate in HomeCA and publish the CRL. The retained
certificate record remains available for audit. See [OPERATIONS.md](OPERATIONS.md)
for backups and operational recovery.
