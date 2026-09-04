# Install trust for the root CA

Install only the HomeCA **root CA** in trust stores. The intermediate CA is
normally served as part of the TLS certificate chain and does not belong in a
root store.

## Download and verify

Download PEM for Linux, macOS, Firefox and appliances, or DER/CER for Windows:

```text
GET /api/v1/trust-anchor/pem
GET /api/v1/trust-anchor/der
```

Obtain and compare the SHA-256 fingerprint through an independent administrator
channel before trusting the certificate.

## Platforms

- **Windows:** import the CER into *Trusted Root Certification Authorities*;
  use Group Policy for Active Directory fleets.
- **Debian/Ubuntu and Proxmox:** place the PEM under
  `/usr/local/share/ca-certificates/` and run `update-ca-certificates`.
- **macOS:** import the PEM into the System keychain and mark it trusted.
- **Firefox:** import manually or distribute an enterprise policy.
- **OPNsense:** use *System > Trust > Authorities* and import an existing CA.
- **Mobile devices, UniFi, switches and Home Assistant:** use the platform's
  documented system or application trust-store import path.

## Verify

Test a HomeCA-protected service from the target device. On Linux, for example:

```bash
openssl s_client -connect service.example.lan:443 -verify_return_error
```

The browser or client must show a valid chain without a trust warning. If it
does not, verify that the installed root fingerprint is correct and that the
service delivers its intermediate chain.
