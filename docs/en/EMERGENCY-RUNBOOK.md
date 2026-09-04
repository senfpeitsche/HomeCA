# Emergency Runbook

This guide is for normal homelab operations. Before each intervention, record the time, affected names, and actions in the audit log or operations journal.

## Certificate or private server key compromised

1. Find and revoke the affected certificate in the inventory.
2. Generate a CRL and ensure it is available at the URL embedded in certificates.
3. Issue a replacement certificate with a new key, install it on the target, and reload the service.
4. Remove the old key and deployment packages from the target and working directories.
5. Verify the service with `openssl s_client` or a browser and close the incident.

Not every client evaluates CRLs. Replacing the certificate on the affected service is therefore the essential action.

## DNS connector token or ACME access compromised

1. Immediately revoke or rotate the token with the DNS provider.
2. Update the connector with the new secret and run both the permission and TXT tests.
3. Renew external ACME certificates for especially important names.
4. Check for unknown DNS records or ACME orders.

## HomeCA host or administrator access compromised

1. Disconnect HomeCA from the network or block access through the firewall/reverse proxy. Do not continue to use it as a trusted CA.
2. From a clean administration system, rotate passwords and DNS tokens.
3. If CA keys may have been accessed, create at least a new issuing CA and replace every active certificate.
4. If the root key may have been accessed, treat the root CA as compromised: build a new PKI and distribute the new root trust in a controlled manner.
5. Fix the cause on the host and restore only from a verified backup or a fresh installation.

## HomeCA container or data volume lost

1. Create a new, current container using the LXC guide, but do not initialize HomeCA yet.
2. Obtain the backup key from its separate secure storage.
3. Verify the backup, stop the service, and restore it into an empty data directory. See [Operations](OPERATIONS.md).
4. Set ownership to `homeca:homeca`, start the service, and verify `/health`, the CA inventory, CRL, and a test certificate.
5. Only then re-enable the reverse proxy or LAN access.

## Backup key lost

An HCAB1 backup cannot be restored without its matching 32-byte key. Look for a separately stored key copy. If none exists, only a readable existing data directory or rebuilding the PKI and redistributing trust remains.

## Regular test

At least quarterly, perform a restore test in an isolated test LXC: verify and restore a backup, compare the CA fingerprint with production, and issue a test certificate. Record the result and date.
