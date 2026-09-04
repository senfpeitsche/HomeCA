# ACME setup

HomeCA supports two ACME modes: its internal RFC 8555 server, which issues
certificates through HomeCA's TLS issuing CA, and registrations for external
ACME issuers such as Let's Encrypt. External issuers use DNS-01 through a
configured DNS connector.

## Prerequisites

- HomeCA is installed and `/health` succeeds.
- The local setup endpoint has configured an administrator.
- The root CA and issuing CA have been initialized:
  `POST /api/v1/authorities/initialize`.

The API examples require a valid session token:

```bash
TOKEN=$(curl -s http://127.0.0.1:5080/api/v1/login \
  -H 'Content-Type: application/json' \
  -d '{"userName":"<admin>","password":"<password>"}' \
  | jq -r '.accessToken')
```

The token is valid for 12 hours.

## Directory URLs

| Interface | Path | Intended use |
| --- | --- | --- |
| **RFC 8555** | `/acme/directory` | Standard ACME clients such as acme.sh, Certbot, OPNsense, Caddy, Traefik and win-acme. |
| **Simplified API** | `/api/v1/acme/directory` | Direct curl/API use with a bearer token; it does not use JWS. |

Use the RFC 8555 directory URL with standard clients:

```text
http://<hostname>:<port>/acme/directory
```

After TLS activation, use the HTTPS address to which the HomeCA UI redirects
(normally `https://<hostname>:5443`). A reverse proxy on port 443 usually
exposes `https://<hostname>/acme/directory`.

Examples:

```bash
certbot certonly --server http://homeca.lab.example.com:5080/acme/directory \
  --manual --preferred-challenges http -d node1.lab.example.com

acme.sh --issue --server http://homeca.lab.example.com:5080/acme/directory \
  -d node1.lab.example.com --standalone
```

HomeCA provides an `http-01` challenge for RFC 8555. Once the client confirms
it, HomeCA marks the challenge and authorization as `valid`; HomeCA does not
perform an external HTTP or DNS reachability check. Configure **HTTP-01** at
the client; public Internet reachability is not required.

For external issuers, `directoryUrl` is the public CA URL, not a HomeCA URL:

| CA | Directory URL |
| --- | --- |
| Let's Encrypt production | `https://acme-v2.api.letsencrypt.org/directory` |
| Let's Encrypt staging | `https://acme-staging-v02.api.letsencrypt.org/directory` |
| ZeroSSL | `https://acme.zerossl.com/v2/DV90` |
| Google Trust Services | `https://dv.acme-v02.api.pki.goog/directory` |
| Buypass production | `https://api.buypass.com/acme/directory` |
| Buypass staging | `https://api.test4.buypass.no/acme/directory` |

## Internal ACME server: simplified API

The simplified API is intended for scripts and curl, not standard ACME clients.
It issues only names that match an internal issuance zone. Orders immediately
enter `ready`, because clients are trusted and no challenge is validated.

### Create an issuance zone

```bash
curl -s http://127.0.0.1:5080/api/v1/domains \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"lab.example.com","internalIssuanceEnabled":true,"connectorId":null}'
```

Repeat this for independent zones. Only domains with
`internalIssuanceEnabled: true` are eligible.

### Register an account and create an order

The directory and account-registration endpoints do not require a bearer token.
Account registration is idempotent for the same contact.

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/accounts \
  -H 'Content-Type: application/json' -d '{"contact":"admin@lab.example.com"}'

curl -s http://127.0.0.1:5080/api/v1/acme/orders \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"accountId":"<account-id>","identifiers":["node1.lab.example.com"]}'
```

All identifiers must be within an active issuance zone. Finalize the returned
order with the desired validity and key algorithm:

```bash
curl -s http://127.0.0.1:5080/api/v1/acme/orders/<order-id>/finalize \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"validityDays":365,"keyAlgorithm":"ECC","rsaKeySize":2048}'
```

| Parameter | Default | Allowed values |
| --- | --- | --- |
| `validityDays` | 365 | 1–730 |
| `keyAlgorithm` | `ECC` | `ECC` (P-256) or `RSA` |
| `rsaKeySize` | 2048 | 2048 or 3072, for RSA only |

A successful order becomes `valid` and contains `certificateId`. Its exports
are in `exports/<certificateId>/`: `certificate.pem` is the leaf certificate
and `chain.pem` contains the issuing and root CA chain. Verify a downloaded
chain with:

```bash
openssl verify -CAfile chain.pem certificate.pem
```

## External ACME issuers

Create a DNS connector first (`technitium` or `hetzner`), then use its ID when
registering the external issuer:

```bash
curl -s http://127.0.0.1:5080/api/v1/connector-instances \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"Hetzner production","type":"hetzner","secrets":{"apiToken":"<token>"}}'

curl -s http://127.0.0.1:5080/api/v1/acme/external-issuers \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"Lets Encrypt Production","directoryUrl":"https://acme-v2.api.letsencrypt.org/directory","connectorId":"<connector-id>"}'
```

`directoryUrl` must be a valid HTTPS endpoint and issuer names must be unique.
List configured issuers with `GET /api/v1/acme/external-issuers`.

## OPNsense ACME client

Install `os-acme-client`, then enable it under **Services > ACME Client >
Settings**. Keep **Auto Renewal** enabled.

Create an account under **Services > ACME Client > Accounts**:

| Field | Value |
| --- | --- |
| Enabled | enabled |
| Name | A freely chosen name, e.g. `HomeCA` |
| E-Mail Address | Contact address |
| ACME CA | **Custom CA URL** |
| Custom CA URL | `http://homeca.lab.example.com:5080/acme/directory` |

Save it, then run **Register** for the account. If its source network is not
allowlisted by HomeCA, enter the HomeCA EAB **Key Identifier** and **HMAC Key**.
Do not use `/api/v1/acme/directory`: acme.sh requires the RFC 8555 endpoint.

Create a challenge type under **Challenge Types** with **HTTP-01** and
**OPNsense Web Service (automatic port forward)**. Then create a certificate
under **Certificates**, select the account and challenge type, and run
**Issue**. Use `ec-256` unless RSA is specifically required.

Optionally create an automation such as **Restart OPNsense Web UI** or
**Restart HAProxy**. Without it, a renewed certificate may be written but not
loaded by the running service. Assign the issued certificate to the relevant
service afterwards. For the OPNsense web UI, choose it under **System >
Settings > Administration > SSL Certificate**.

Import the HomeCA root CA into **System > Trust > Authorities** using
**Import an existing Certificate Authority**. See
[TRUST-INSTALLATION.md](TRUST-INSTALLATION.md) for client trust installation.

## Access policy and operations

HomeCA allows RFC 8555 account registration either from an allowlisted direct
client network or with an External Account Binding (EAB) credential using
`HS256`. Create a separately named EAB credential per client; its HMAC key is
shown only on creation and registers exactly one account.

```bash
# Show current policy
curl -s -H "Authorization: Bearer $TOKEN" \
  http://homeca.lab.example.com:5080/api/v1/acme/access-policy

# Allow direct client addresses or CIDRs
curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"allowlistedClientNetworks":["192.168.10.25","192.168.20.0/24"]}' \
  http://homeca.lab.example.com:5080/api/v1/acme/access-policy

# Create an EAB credential
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"opnsense-fw"}' \
  http://homeca.lab.example.com:5080/api/v1/acme/access-policy/eab-credentials
```

The allowlist deliberately evaluates the direct TCP peer IP, not forwarded
headers. Do not broadly allowlist a reverse proxy: it would let all proxied
clients bypass EAB. Use EAB or enforce a restrictive proxy access policy.

- Monitor `GET /api/v1/warnings/expiring` daily for certificates expiring
  within 30 days.
- Create a verified backup after ACME setup; see [OPERATIONS.md](OPERATIONS.md).
- Test DNS connector permissions and TXT operations before assigning an
  external issuer.
- Treat the 12-hour bearer token and EAB HMAC keys as credentials; never store
  or share them permanently.
- The HomeCA ACME detail views expose orders, authorizations, challenge status,
  account fingerprints and contacts, but never challenge tokens.
