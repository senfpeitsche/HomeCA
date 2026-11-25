## Context

- Management API: all groups under `/api/v1` use `BearerTokenFilter`, which validates an administrator
  session from `LocalAdministrationService` (12 h sessions, hashed in state).
- Deployment targets already use their own per-target token: `DeploymentTargetRegistry` stores
  `SHA256` hex hashes and compares with `CryptographicOperations.FixedTimeEquals`; agent endpoints live in a
  separate group `/api/v1/deploy/{id}` without `BearerTokenFilter`.
- Issuance: `CertificateIssuanceService` with `IssueCertificateApiRequest` (Usage, DnsNames, IpAddresses,
  ValidityDays, KeyAlgorithm, …, AutoRenew) and `SignCsrRequest`; there is exactly one issuing CA.
- Audit: `LocalAdministrationService.RecordAuditAsync(action, subject)` appends NDJSON events.
- Name constraints exist for intermediate CAs but do not help here, because issuance always uses the single
  issuing CA.

## Goals / Non-Goals

**Goals:**
- A credential that can only issue/sign/export certificates for an administrator-defined set of names.
- Leak of the credential must not allow CA administration, revocation, or access to other certificates.
- Reuse existing patterns (hashed tokens, separate route group, audit).

**Non-Goals:**
- No general role/permission model for administrator accounts.
- No multiple issuing CAs or per-token issuer selection.
- No auto-renew for token-issued certificates (the client renews by issuing again).
- No revocation through the token (administrator only).

## Decisions

### D1: Separate route group instead of extending `BearerTokenFilter`
`/api/v1/issuance` gets its own `IssuanceTokenFilter`. The management filter stays untouched, so a token can
never reach a management endpoint by mistake. *Alternative:* a role claim inside sessions – rejected, bigger
change and easy to get wrong on existing endpoints.

### D2: Token format and storage
Secret: 32 random bytes, Base64Url, prefixed `hca_it_` (recognizable in logs and secret scanners). Stored:
`Id`, `Name`, `TokenHash` (SHA-256 hex), `AllowedDnsSuffixes`, `AllowedIpCidrs`, `AllowedUsages`,
`MaxValidityDays`, `ExpiresAt?`, `CreatedAt`, `CreatedBy`, `LastUsedAt?`, `RevokedAt?`. File
`state/issuance-tokens.json`, written like other registries (lock + atomic replace). Lookup by hash with
`FixedTimeEquals`. The secret is returned once in the create response and never again.

### D3: Scope enforcement before issuance
`IssuanceTokenFilter` resolves the token and puts the record into `HttpContext.Items`. The endpoint validates
the request against the scope before calling `CertificateIssuanceService`:
- each DNS name (lower-cased, trailing dot removed) equals a suffix or ends with `.` + suffix; wildcards only if
  the suffix rule matches the part after `*.`,
- each IP SAN inside an allowed CIDR, otherwise rejected,
- `ValidityDays` ≤ `MaxValidityDays`, usage in `AllowedUsages`, `AutoRenew` forced to `false`.
For CSRs, names are taken from the request **and** from the CSR's SAN/CN, and all of them must match – the CSR
cannot smuggle names past the check. Violations return `403` with a precise reason and are audited.

### D4: Ownership of certificates
The certificate record gets an optional `IssuedByTokenId`. `GET`, PFX export and full-chain export under `/issuance` only work for
certificates with the caller's token id; others return `404` (no information leak). The management UI shows
the token name next to such certificates.

### D5: Revocation and expiry
Revoking or expiring a token blocks further use immediately; certificates already issued stay valid (the
administrator can revoke them in the normal UI). `LastUsedAt` is updated at most once per minute to limit
writes.

### D6: Audit
Actions: `issuance-token.created`, `.revoked`, `.issued`, `.csr-signed`, `.exported`, `.denied`. Subject
contains token name/id, certificate id and names – never the secret or PFX password.

### D7: Rate limiting
Reuse the existing rate limiter infrastructure: per token, e.g. 30 issuance requests per hour (configurable).
Invalid tokens are throttled per client IP like failed logins.

## Risks / Trade-offs

- [Token in a reset VM is stored on disk] → narrow scope, short `MaxValidityDays`, optional token expiry,
  revocation in one click; secret prefix makes accidental commits detectable.
- [Suffix check bypass via IDN/case/trailing dot] → normalize (IDN to ASCII, lower case, strip trailing dot)
  before comparing; tests for these cases.
- [CSR contains extra names] → check CN and all SANs from the CSR, not only the request fields.
- [More state to back up] → file lives under `state/`, already part of backup/restore.

## Migration Plan

Additive. Existing state without `issuance-tokens.json` means "no tokens". Rollback: remove the feature; the
optional `IssuedByTokenId` field is ignored by older versions.

### D8: Defaults and notifications
The create dialog proposes `MaxValidityDays = 30`; that is enough for test use. Token-issued certificates are
excluded from expiry warnings and notifications (`Operations` expiry warnings, notification channels, the
"needs attention" overview), because they are short-lived and replaced by the client.

## Open Questions

- none
