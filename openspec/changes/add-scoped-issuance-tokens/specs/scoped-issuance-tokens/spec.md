## ADDED Requirements

### Requirement: Administrators manage issuance tokens
The system SHALL let an authenticated administrator create, list and revoke issuance tokens in the web UI and
via the management API. A token SHALL have a name, allowed DNS suffixes, optional allowed IP CIDRs, allowed
usages, a maximum validity in days not exceeding 730, and an optional expiry date. The token secret SHALL be
shown exactly once on creation and only its SHA-256 hash SHALL be stored.

#### Scenario: Create token
- **WHEN** an administrator creates a token for suffix `abnahme.home.lan` with a maximum validity of 30 days
- **THEN** the response contains a secret starting with `hca_it_`
- **AND** the stored state contains only the hash of that secret

#### Scenario: Secret not retrievable later
- **WHEN** an administrator lists or opens an existing token
- **THEN** the secret is not shown, only name, scope, creation, last use and status

#### Scenario: Revoke token
- **WHEN** an administrator revokes a token
- **THEN** every following request with that token is rejected with `401`

### Requirement: Tokens are limited to the issuance API
The system SHALL accept issuance tokens only on the `/api/v1/issuance` route group. Management endpoints SHALL
keep requiring an administrator session and MUST reject issuance tokens.

#### Scenario: Token on management endpoint
- **WHEN** a client calls `POST /api/v1/authorities/initialize` or `POST /api/v1/certificates/{id}/revoke`
  with an issuance token
- **THEN** the request is rejected with `401`

#### Scenario: Expired token
- **WHEN** a client uses a token after its expiry date
- **THEN** the request is rejected with `401` and an audit event is written

### Requirement: Issuance within scope
The system SHALL issue a certificate via `POST /api/v1/issuance/certificates` only if every DNS name equals an
allowed suffix or is a subdomain of one, every IP SAN lies in an allowed CIDR, the usage is allowed and the
validity does not exceed the token's maximum. Names SHALL be normalized (IDN to ASCII, lower case, trailing dot
removed) before comparison. Automatic renewal SHALL be disabled for token-issued certificates.

#### Scenario: Name within scope
- **WHEN** a client requests a certificate for `mp.abnahme.home.lan` with 2 days validity
- **THEN** the certificate is issued and recorded with the token id

#### Scenario: Name outside scope
- **WHEN** a client requests a certificate for `mail.home.lan`
- **THEN** the request is rejected with `403` naming the disallowed name
- **AND** an audit event `issuance-token.denied` is written

#### Scenario: Suffix trick
- **WHEN** a client requests `evilabnahme.home.lan` or `abnahme.home.lan.attacker.tld`
- **THEN** the request is rejected with `403`

#### Scenario: Validity too long
- **WHEN** a client requests 365 days with a token limited to 30 days
- **THEN** the request is rejected with `403`

### Requirement: CSR signing within scope
The system SHALL sign a CSR via `POST /api/v1/issuance/certificates/csr` only if the subject CN and all SANs of
the CSR as well as any names in the request satisfy the token scope.

#### Scenario: CSR with extra SAN
- **WHEN** a CSR contains the allowed name `mp.abnahme.home.lan` and additionally `intranet.home.lan`
- **THEN** the request is rejected with `403` naming `intranet.home.lan`

#### Scenario: Valid CSR
- **WHEN** a CSR contains only allowed names
- **THEN** the signed certificate is returned and recorded with the token id

### Requirement: Access only to own certificates
The system SHALL allow `GET /api/v1/issuance/certificates/{id}` and
`POST /api/v1/issuance/certificates/{id}/export/pfx` and `GET /api/v1/issuance/certificates/{id}/export/fullchain`
only for certificates issued with the calling token.

#### Scenario: Foreign certificate
- **WHEN** a client requests details, PFX or full-chain export of a certificate issued by an administrator or another token,
  or of an unknown id
- **THEN** the response is `404`

#### Scenario: Own certificate export
- **WHEN** a client exports a PFX for a certificate it issued, with a password
- **THEN** the PFX is returned and an audit event `issuance-token.exported` is written

#### Scenario: Full chain of a CSR certificate
- **WHEN** a client requests `export/fullchain` for a certificate it had signed from a CSR
- **THEN** the PEM certificate and its issuer chain are returned without any private key, and an audit event
  `issuance-token.exported` is written

### Requirement: No expiry notifications for token-issued certificates
The system SHALL exclude certificates issued with an issuance token from expiry warnings, notifications and
the "needs attention" overview. The create dialog SHALL propose a maximum validity of 30 days.

#### Scenario: Short-lived test certificate expires
- **WHEN** a certificate issued with a token is within its expiry warning window or expired
- **THEN** no notification is sent and it does not appear in `GET /api/v1/attention`

#### Scenario: Default validity
- **WHEN** an administrator opens the create dialog
- **THEN** the maximum validity field is prefilled with 30

### Requirement: Auditing and rate limiting
The system SHALL write audit events for token creation, revocation, issuance, CSR signing, export and denied
requests, without secrets or PFX passwords. The system SHALL limit the number of issuance requests per token
and throttle invalid tokens per client address.

#### Scenario: Audit without secrets
- **WHEN** any issuance-token action is audited
- **THEN** the event contains token name/id and certificate data but not the token secret or PFX password

#### Scenario: Rate limit
- **WHEN** a token exceeds the configured number of issuance requests per hour
- **THEN** further requests are rejected with `429` until the window resets
