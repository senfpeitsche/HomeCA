# Install the trust anchor

Install the HomeCA Root CA in each client trust store. Use the PEM endpoint for Unix-like systems and the CER endpoint for Windows.

When the HTTPS server certificate is issued by this very Root CA, the first
download is a bootstrap step: verify the Root CA fingerprint through an
independent administrative channel first, then use `--insecure` for this
initial download only.

```sh
curl --fail --show-error --location --insecure -o homeca-root-ca.pem https://HOMECA:5443/api/v1/trust-anchor/pem
```

> Install the Root CA in the trust store. Deliver Intermediate CAs with TLS certificate chains instead.

## Platform guidance

On Debian or Ubuntu, copy the PEM file to `/usr/local/share/ca-certificates/` and run `update-ca-certificates`. Proxmox nodes normally run these commands as `root`, so do not use `sudo` there. On Windows, import the CER file into **Local Computer → Trusted Root Certification Authorities**. Distribute the Root CA through Group Policy for managed Windows devices.

Windows also needs a one-time bootstrap download for an HTTPS instance because the server certificate cannot be validated before the Root CA is installed. Run the copied instructions as an administrator; they use the certificate exception only for the first download and verify the endpoint without an exception afterwards.

| Platform | Trust-anchor action |
| --- | --- |
| Debian / Ubuntu | Install PEM under `/usr/local/share/ca-certificates/`, then run `update-ca-certificates` |
| Windows | Import CER into the Local Computer Root store |
| Active Directory | Deploy the Root CA with Group Policy |
| macOS | Import the Root CA into the System keychain and mark it trusted |
| Firefox | Use the enterprise policy or import into its managed trust store |

For SSH, trust is configured separately: distribute the SSH host CA through `known_hosts`, and configure `TrustedUserCAKeys` on servers that accept user certificates.

## Verify

After installing the Root CA, reconnect to an internal TLS service and verify that the client accepts its certificate chain without an untrusted-issuer warning.

Download trust anchors only from the expected HomeCA address and verify the certificate subject or fingerprint through an independent administrative channel before distributing them widely.

Do not import an Intermediate CA into the Root trust store unless a target product explicitly requires that exceptional configuration.

Limit Root CA distribution to managed clients and services that need to validate HomeCA-issued certificates.

After Group Policy deployment, verify one representative client has received the Root CA before relying on it for service rollout.

If a trust deployment causes unexpected client behavior, pause the rollout and remove only the newly distributed anchor from the affected policy after verifying the target scope.

Use a representative internal HTTPS service to verify that the installed Root CA validates the complete chain.

Keep an inventory of systems that received the Root CA so a later replacement or removal can be scoped safely.
