# Operating HomeCA

## Post-deployment checks

1. Confirm `GET /health` returns successfully.
2. Set up the administrator from a trusted administration network and sign in. After initial setup, enable TLS and restrict access with a firewall or reverse proxy.
3. Initialize the CAs and issue a test certificate for an internal issuance zone.
4. Verify the PEM chain with `openssl verify` against the exported root CA.
5. For every DNS connector, run the permission test first and the TXT test second.

## Expiry warnings

`GET /api/v1/warnings/expiring` returns certificates expiring within 30 days. Poll it at least daily through a local monitoring job and forward critical warnings to the administrator before expiry.

## Rotate an intermediate CA

Do **not** revoke a TLS intermediate that is expiring normally. Create a replacement intermediate under the same root early, make it the issuing CA, and deploy every certificate issued or renewed afterwards together with its new chain. Keep the former intermediate disabled but available until the last certificate it issued has expired or been replaced. The root CA remains unchanged and does not need redistribution.

An intermediate must expire before its root CA. A TLS certificate must not outlive its issuing intermediate either. Plan rotation at least as far ahead as the maximum permitted TLS certificate lifetime (currently 730 days).

Every intermediate has its own CRL at `GET /api/v1/crl/<authority-id>`, embedded in newly issued certificates. Renew and redeploy certificates that received the general `GET /api/v1/crl/latest` endpoint before this change.

## Renewal email notifications

Enable email notifications under **Renewal automation** to receive a message after a certificate is renewed automatically or an automatic renewal fails. Use **Send test email** to validate the configuration before production.

- **SMTP:** Works with any TLS-protected SMTP server. Microsoft 365 normally uses `smtp.office365.com` on port `587`; SMTP authentication must be allowed for the sender mailbox.
- **Microsoft 365 through Graph:** The app registration needs the Microsoft Graph application permission `Mail.Send` with administrator consent. Store the tenant ID, client ID, client secret, and sender mailbox in HomeCA.

Passwords and client secrets are processed only when entered and are not returned by the management API. Stored configuration resides in the protected HomeCA data directory; restrict it to the service account.

## Backup and restore

Create an encrypted backup through `POST /api/v1/backups`, then verify it with `POST /api/v1/backups/{fileName}/verify`. Verification decrypts the archive and reads its ZIP contents without changing running state.

For restore, stop the service, save the current data directory separately, decrypt the verified HCAB1 archive using the 32-byte backup key, and extract it into an empty data directory. Set ownership back to `homeca:homeca`, start the service, then verify `/health`, the CA inventory, and a test chain. Never restore into a live data directory.

## Debian LXC

The LXC needs .NET 10, the OpenSSH client for SSH CAs, and writable storage for `/var/lib/homeca` and `/var/backups/homeca`. The unit in `deploy/systemd/homeca.service` manages the service. Before production, restrict data and backup-key file permissions to the service account.
