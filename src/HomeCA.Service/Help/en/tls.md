# Issue TLS certificates

Use **Certificates** to select a TLS profile, enter the DNS names, and issue a certificate. Install the generated certificate and chain on the target service.

> The Root CA must be trusted by the clients that connect to the service.

## Verify

```sh
openssl s_client -connect service.example:443 -showcerts
```

| Item | Recommendation |
| --- | --- |
| Key | ECC P-256 |
| Renewal | Configure it before expiry |

## Deployment package

The ZIP deployment package contains the certificate, private key, chain, checksums, installation guidance, and the renewal-script snapshot. Treat it like the private key: transfer it only over a protected channel and remove it after installation.

Use the individual exports when the target requires separate files:

| Export | Typical use |
| --- | --- |
| PEM | Linux web services |
| Key | Private key alongside PEM |
| Chain / Fullchain | Services that require the issuing chain |
| PFX | Windows and Java keystores |

## Renewal and revocation

Create a renewal plan after validating the installation. HomeCA renews active plans with the original certificate settings. If a key is suspected to be compromised, revoke the certificate from the inventory and deploy a replacement; the CRL is then updated.

For automated checks, use `GET /api/v1/warnings/expiring` to review certificates approaching expiry. Certificate CRLs are published through the certificate's configured CRL distribution point; do not replace that endpoint with a translated value.

When deploying a server certificate, include the issuing chain required by the service. Test from a separate client after restarting or reloading the service so cached certificates do not hide an incomplete chain.

Restrict access to private-key files to the service account and keep backups encrypted.

Record the deployed certificate serial number so it can be located quickly in the inventory during an incident.

## Issuance parameters

Use `TLS-Server` for server certificates and `mTLS / Client` for client authentication. Supply comma-separated DNS names; wildcard names such as `*.home.lab` are supported. IP SANs depend on the selected target profile. Validity is limited by that profile, and some profiles require RSA rather than ECC.

| Target profile | Default key | Exports | Note |
| --- | --- | --- | --- |
| Generic TLS | ECC | PEM, PFX | General-purpose profile |
| Windows IIS | RSA | PFX | IIS binding and RDP |
| Proxmox VE | ECC | PEM | No IP SAN |
| OPNsense | RSA | PFX, PEM | Firewall web UI |
| Home Assistant | ECC | PEM | No IP SAN |
| UniFi OS | ECC | PEM | No IP SAN |
| HAProxy | ECC | PEM | Bundle export |
| nginx | ECC | PEM | Fullchain for `ssl_certificate` |
| Cisco Switch | RSA | PEM, PFX | PKCS12 CLI import |
| Synology DSM | RSA | PEM | No IP SAN |

`Chain` contains the issuing and Root CA; `Fullchain` combines the certificate and chain; `Bundle` combines key, certificate, and chain for services such as HAProxy.

For API issuance, preserve field names such as `dnsNames`, `ipAddresses`, `validityDays`, `keyAlgorithm`, `rsaKeySize`, and `targetProfileId` exactly as documented.

The ZIP package is not an additional encryption boundary; it includes private-key material and must be handled accordingly.

When a target profile requires RSA, do not override it with ECC; the profile encodes compatibility requirements for that target.

Validate a renewed certificate on the target service before retiring the previous deployment artifact.

Ensure clients trust the Root CA before diagnosing a TLS deployment as a server-certificate failure.

Review expiry warnings regularly and resolve them through renewal plans before certificates approach their operational deadline.

Retain the previous known-good deployment until the replacement certificate has been independently verified.

Use the installation guidance included with the selected target profile; it captures format and service-specific deployment requirements.
