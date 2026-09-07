# Issue SSH certificates

Select an SSH profile, provide the public key and principals, then distribute the issued certificate alongside the public key.

| Field | Host certificate | User certificate |
| --- | --- | --- |
| Identity | Server or service name | Person or automation identity |
| Principals | Accepted host names | Accepted login names |
| Public key | Host public key | User public key |
| Trust on verifier | `known_hosts` | `TrustedUserCAKeys` |

```sh
ssh -i ~/.ssh/id_ed25519 user@host
```

Keep the SSH CA public key in the server's `TrustedUserCAKeys` or `TrustedHostCAKeys` configuration.

## Issue and deploy

Issue the certificate only after checking the public-key format and intended principals. Put the resulting host certificate beside the host key and configure `HostCertificate` in `sshd_config`. Put a user certificate beside its private key; OpenSSH then discovers it through the standard filename convention.

## Establish trust

HomeCA uses separate CAs for host and user certificates. Add the host CA to client `known_hosts` files, and configure the user CA with `TrustedUserCAKeys` in `sshd_config` on every accepting server.

```sh
echo "@cert-authority *.home.lab ssh-ed25519 AAAA..." >> ~/.ssh/known_hosts
sudo systemctl reload sshd
```

Use `ssh-keygen -L -f certificate.pub` to inspect an issued certificate. Replace and revoke a certificate if its private key is lost or suspected to be compromised.

Issue a replacement before the current certificate expires, install it beside the corresponding key, and test one connection before removing the previous certificate. Keep the CA public key stable during routine certificate renewal.

Review the certificate validity interval in `ssh-keygen -L` output before deployment.

Ensure every configured principal matches the intended host or user identity.

Treat host and user CA trust independently; changing one does not configure trust for the other.

Verify that the certificate is installed beside the exact public/private key pair it was issued for.

Validate the `sshd_config` syntax before reloading `sshd` to avoid interrupting remote access.

After replacing a host certificate, reconnect from a client that trusts the host CA and verify that no host-key prompt appears.

For user certificates, verify the target server recognizes the intended principal before removing existing access methods.

Keep the issuance profile and its configured validity policy aligned with the access policy of the target system.
