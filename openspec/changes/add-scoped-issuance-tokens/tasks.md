## 1. Token registry

- [x] 1.1 `IssuanceTokenRegistry` with records (id, name, hash, DNS suffixes, IP CIDRs, usages, max validity, expiry, created/last used/revoked) in `state/issuance-tokens.json`, lock + atomic write
- [x] 1.2 Create (secret `hca_it_` + 32 random bytes Base64Url, return once), list, revoke, resolve by hash with `FixedTimeEquals`
- [x] 1.3 Throttled `LastUsedAt` update (max once per minute)
- [x] 1.4 Verify backup/restore includes the new state file

## 2. Scope checks

- [x] 2.1 Name normalization (IDN to ASCII, lower case, trailing dot) and suffix match (exact or `.`-separated subdomain, wildcard rule)
- [x] 2.2 IP SAN check against CIDRs, usage and validity check, force `AutoRenew = false`
- [x] 2.3 CSR check over CN and all SANs from the CSR plus request names
- [x] 2.4 Unit tests: in scope, out of scope, suffix tricks, IDN/case/trailing dot, IP SAN, validity, CSR with extra SAN

## 3. Issuance API

- [x] 3.1 `IssuanceTokenFilter` (Bearer `hca_it_…`, revoked/expired → 401, record into `HttpContext.Items`, invalid-token throttling per IP)
- [x] 3.2 Route group `/api/v1/issuance` with issue, CSR sign, get own certificate, PFX export of own certificate; reuse certificate id validation filter
- [x] 3.3 `IssuedByTokenId` on certificate records; foreign certificates → 404
- [x] 3.4 Per-token rate limit for issuance requests → 429
- [x] 3.5 Audit events `issuance-token.created|revoked|issued|csr-signed|exported|denied` without secrets
- [x] 3.6 Integration tests: token rejected on management endpoints, revoked/expired token, own vs. foreign certificate, rate limit

## 4. Management

- [x] 4.1 Management endpoints (administrator session): create, list, revoke tokens
- [x] 4.2 Blazor page „Issuance tokens“: create dialog with scope (default max validity 30 days), one-time secret display with copy, list with last use and revoke
- [x] 4.3 Show token name on certificates issued via a token
- [x] 4.5 Exclude token-issued certificates (`IssuedByTokenId` set) from expiry warnings, notification channels and `GET /api/v1/attention`; test
- [x] 4.4 Localized UI strings and help article (en/de); localization completeness tests green

## 5. Documentation

- [x] 5.1 OpenAPI annotations for the issuance group
- [x] 5.2 `docs/SSL-USAGE.md`: section on issuance tokens with curl examples
- [x] 5.3 `docs/THREAT-MODEL.md`: token scope, storage and leak handling
