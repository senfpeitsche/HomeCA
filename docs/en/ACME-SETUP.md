# ACME setup

HomeCA provides an RFC 8555-compatible internal ACME server and can manage external ACME issuers through DNS-01. There is no simplified `/api/v1/acme/` ACME API.

## Internal ACME server

Standard clients such as Proxmox, OPNsense, Caddy, Traefik, Certbot, and acme.sh use this directory URL only:

```text
https://<homeca-host>:5443/acme/directory
```

Behind a reverse proxy on port 443 it is normally `https://<homeca-host>/acme/directory`. It must match HomeCA's public URL configured under **Settings**.

### Prerequisites

1. The Root CA and TLS Issuing CA are active.
2. A domain under **Domains** has internal issuance enabled.
3. The ACME client trusts the HomeCA Root CA when the directory uses HTTPS.
4. The client can create an account through its direct IP allowlist or a dedicated EAB credential.

### HTTP-01

HomeCA supports HTTP-01 for internal RFC 8555 orders. The ACME client must be reachable at:

```text
http://<DNS-name>/.well-known/acme-challenge/<token>
```

HomeCA fetches and compares the key authorization. The target name must be reachable from the HomeCA host on port 80; public Internet exposure is not required. Reverse proxies must not block or redirect this path.

### Access control

- Fixed internal clients: add the direct client IP or CIDR to the ACME client allowlist.
- Changing client IPs, containers, or Proxmox: create a dedicated EAB credential and store its Key ID/HMAC key in the client's secret store.

Never broadly allowlist a reverse-proxy address; it would let all clients behind it bypass EAB.

After a successful order it is `valid` under **ACME**, and the certificate appears under **Certificates**.

## External ACME issuers

Use external issuers only for publicly trusted certificates. Configuration, issuer inventory, and external certificates live in the separate **External ACME** workspace. Register the issuer there and assign a DNS connector. Typical directory URLs:

| Issuer | Directory URL |
| --- | --- |
| Let's Encrypt production | `https://acme-v02.api.letsencrypt.org/directory` |
| Let's Encrypt staging | `https://acme-staging-v02.api.letsencrypt.org/directory` |
| ZeroSSL | `https://acme.zerossl.com/v2/DV90` |
| Google Trust Services | `https://dv.acme-v02.api.pki.goog/directory` |

Test connector permissions and the TXT round trip before the first order. The connector needs permission to manage `_acme-challenge` TXT records in the target zone.

## IP SANs and renewal

The ACME policy can add resolved A and AAAA addresses of validated DNS names as IP SANs. This happens only inside the configured CIDR allowlist; arbitrary client-requested IP addresses are never accepted.

Every renewal issues a new certificate. Its predecessor remains valid by default so deployments can switch safely. Optionally, the policy can revoke the matching predecessor of the same ACME account with identical DNS identifiers after a successful renewal. Revoked certificates remain in the separate table for CRL and audit purposes.

## Troubleshooting

| Problem | Check |
| --- | --- |
| Account creation fails | direct client IP allowlist or EAB Key ID/HMAC key |
| Order remains `pending` | DNS resolution, port 80, and challenge path from the HomeCA host |
| Directory HTTPS fails | Root CA in the client trust store |
| External order fails | DNS connector test, zone, and TXT permission |

Open the existing order in HomeCA before creating another account or EAB credential. Its account, order, and challenge status provide the relevant evidence.
