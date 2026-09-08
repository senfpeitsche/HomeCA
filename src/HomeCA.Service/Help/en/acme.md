# ACME in practice

ACME automates issuance and renewal. Use the **internal HomeCA ACME server** for managed internal services such as Proxmox, Traefik, or Caddy. Use an **external ACME issuer** such as Let's Encrypt only when an Internet-facing service needs public browser trust.

## Choose the correct URL

Standard ACME clients always need the RFC 8555 directory:

```text
{{ACME_DIRECTORY_URL}}
```

Generally this is `https://<HomeCA-host>:5443/acme/directory`; behind a reverse proxy it is normally `https://<HomeCA-host>/acme/directory`.

> Do **not** use `/api/v1/acme/...` in Proxmox, Caddy, Traefik, Certbot, or acme.sh. These clients require `/acme/directory`.

When HomeCA HTTPS uses its own Root CA, the ACME client must trust that Root CA already. Install it first from **Trust**.

## Scenario: Proxmox or an internal service

1. The Root CA and TLS Issuing CA must be active.
2. Under **Domains**, create an issuance zone and enable internal issuance, such as `int.example.org`.
3. Configure the directory URL in the client and select **HTTP-01**.
4. HomeCA must reach the target name on port 80. The client serves:

```text
http://<DNS-name>/.well-known/acme-challenge/<token>
```

5. The client installs the certificate and reloads its service.

No public Internet exposure is required. Behind a reverse proxy, `/.well-known/acme-challenge/` must not be blocked or redirected.

## Client access: allowlist or EAB

- For a fixed internal client, add its **direct** IP address or CIDR network to the ACME client allowlist.
- For Proxmox, containers, or changing client addresses, create a dedicated **EAB** credential. Copy the Key ID and HMAC key into the client's secret store immediately; the HMAC key is shown only once.

Never broadly allowlist a reverse-proxy address. It would give all clients behind that proxy access without EAB.

After a test, the order under **ACME** must be `valid`. The issued certificate appears under **Certificates**.

## Scenario: publicly trusted certificates

Register an external issuer under **ACME**, for example:

```text
https://acme-v02.api.letsencrypt.org/directory
```

Assign a DNS connector and test its permission and TXT round trip before the first order. The connector needs permission to create and remove `_acme-challenge` TXT records in the zone.

## IP SANs and renewal

The ACME policy can contain allowed IP networks. When enabled, HomeCA adds only A and AAAA addresses of validated DNS names that fall inside those CIDR networks. Arbitrary client-requested IP addresses are never accepted.

A renewal issues a new certificate. The predecessor normally remains valid until expiry. The policy can optionally revoke the matching predecessor after a successful renewal. Revoked certificates remain visible for audit and CRL purposes in **Revoked certificates**.

## Diagnose failures quickly

| Symptom | Check first |
| --- | --- |
| Account cannot be created | direct client IP in allowlist, or EAB Key ID/HMAC key |
| Order remains `pending` | DNS resolution, port 80, and challenge path from the HomeCA host |
| HTTPS to directory fails | HomeCA Root CA in the client trust store |
| External order fails | DNS connector test, zone, and TXT permission |

Do not create another account or EAB credential first when diagnosing a failure. Open the existing order in HomeCA; account, order, and challenge status provide the evidence you need.
