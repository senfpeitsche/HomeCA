## Why

Automated clients that need certificates from HomeCA's issuing CA today have only two options: the RFC 8555
ACME flow (which requires HTTP-01 reachability) or the management API with a full administrator session
(`POST /api/v1/login` → `BearerTokenFilter`). An administrator session can operate the whole CA: initialize
or rotate authorities, revoke any certificate, export any private key, change settings. Handing that to a
test VM or a CI job is far too much power.

The concrete trigger is the Mandantenportal acceptance suite: an isolated, regularly reset Windows VM must
issue short-lived TLS certificates for one test host name, sign CSRs created on that VM, and download its own
PFX files. If the VM's stored credential leaks, the damage must stay limited to that test domain.

## What Changes

- New **scoped issuance tokens**: long-lived, revocable API keys that are created by an administrator and
  carry a fixed scope:
  - allowed DNS name suffixes (e.g. `abnahme.home.lan`); every requested DNS SAN must match exactly or be a
    subdomain of one of them; IP SANs are rejected unless explicitly allowed by CIDR,
  - maximum validity in days (≤ the global maximum of 730),
  - allowed usages (default `TLS`),
  - optional expiry date of the token itself.
- Tokens are presented as `Authorization: Bearer hca_it_<secret>` and accepted only on a new, separate route
  group `/api/v1/issuance/…`:
  - `POST /api/v1/issuance/certificates` – issue key + certificate (no auto-renew),
  - `POST /api/v1/issuance/certificates/csr` – sign a CSR,
  - `GET  /api/v1/issuance/certificates/{id}` – details of a certificate issued with this token,
  - `POST /api/v1/issuance/certificates/{id}/export/pfx` – PFX export of a certificate issued with this token.
- Tokens are **never** accepted by `BearerTokenFilter`; existing management endpoints keep requiring an
  administrator session. No existing endpoint or contract changes.
- Only the SHA-256 hash of a token is stored (same pattern as deployment-target tokens); the secret is shown
  once on creation.
- Each certificate issued through a token records the token id; every token use (issue, sign, export, denied
  request) writes an audit event.
- Administrators manage tokens in the web UI (create, show scope, last use, revoke) and via management API.

## Capabilities

### New Capabilities
- `scoped-issuance-tokens`: creation, storage, scope enforcement, revocation and auditing of restricted API
  tokens for certificate issuance, CSR signing and export of own certificates.

### Modified Capabilities
- none

## Impact

- New: `Security/IssuanceTokenRegistry.cs`, `Security/IssuanceTokenFilter.cs`,
  `Endpoints/IssuanceEndpoints.cs`, management endpoints and a Blazor page for tokens, help text (en/de).
- `Pki/CertificateIssuanceService.cs`: optional issuing-token id stored with the certificate record; scope
  check before issuance / CSR signing.
- State: new file under `state/` (token records with hashes); included in existing backups.
- OpenAPI document gains the issuance group.
- No change to ACME, deployment agents, SSH issuance or authority management.
