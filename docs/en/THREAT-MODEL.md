# Threat Model and Trust Boundaries

HomeCA is a private PKI for a homelab. It does not replace an enterprise PKI, HSM, or centrally managed identity system. Its purpose is to operate internal services with simple, traceable certificates.

## Default model

Run HomeCA as a single protected LAN service. The web UI and API may be reachable from the LAN, but must use TLS after initial setup. Restrict access to administrators and trusted automation.

In the simple mode, the root and issuing CA remain online in the protected HomeCA data directory. This is intentionally easy to operate, with one consequence: anyone who takes over the HomeCA system or a fully privileged administrator account can issue certificates within the trust domain.

## What is protected

- Private CA and certificate keys from ordinary LAN clients and unauthorized users.
- Management, exports, connector secrets, and backups through HomeCA authentication and service-account file permissions.
- Certificate trust through a one-time, controlled root-CA distribution.
- Lost or compromised leaf certificates through revocation, CRLs, and replacements.

## What is not solved automatically

- A compromised HomeCA host, stolen administrator access, or unprotected backup can affect the entire PKI.
- An internal ACME issuance zone is a trust boundary. The RFC 8555 endpoint accepts new accounts only from allowlisted client networks or with EAB. An allowlist does not replace network access control; set the source-IP boundary deliberately behind a reverse proxy.
- CRLs help only clients and services that support fetching and evaluating them.
- HomeCA cannot protect private keys after a deployment package has been copied to a target system.

## Mandatory operating rules

1. Never publish HomeCA directly on the Internet. Permit access only from administration or server networks; use a VPN for remote administration.
2. Enable HTTPS after setup. Use HTTP only for local initial setup or deliberately isolated migrations.
3. Verify the root-CA fingerprint outside the certificate download connection, for example in the HomeCA console, a password manager, or a second administration channel. Do not obtain it only from the same URL as the certificate.
4. Make the data directory, backup directory, and backup key readable only by `homeca` or root. Deployment packages contain private keys.
5. Create DNS API tokens with the least possible privileges and rotate them immediately after a suspected leak.
6. Decide deliberately for every RFC 8555 client: allowlist its direct network or use EAB credentials. EAB is the safer default for clients behind a reverse proxy.

## Future hardening

A future hardened mode may provide an offline root CA: unlock the root only to create or rotate an issuing CA, while the running HomeCA service uses only the issuing CA. This increases protection but introduces a manual, documented rotation process.
