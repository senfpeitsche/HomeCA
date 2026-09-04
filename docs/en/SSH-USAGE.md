# Issue and use SSH certificates

SSH certificates replace distributed `authorized_keys` and host fingerprints
with HomeCA-managed host and user CAs.

## Prerequisites

Initialize HomeCA's CAs and retrieve the public host and user CA keys from the
UI or API. Decide principals and short validity periods before issuing.

## Trust setup

On clients, add the host CA to `known_hosts` using an `@cert-authority` entry.
On target servers, copy the user CA public key and configure
`TrustedUserCAKeys` in `sshd_config`. Optionally use `AuthorizedPrincipalsFile`
to limit which principals may log in as each local account. Reload `sshd` after
configuration changes.

## Issue and deploy

Issue host or user certificates through **Certificates > SSH** or the SSH API.
For a host certificate, install the returned `*-cert.pub` file beside the host
key and reference it through `HostCertificate` in `sshd_config`. For a user
certificate, save it beside the matching private key, for example
`~/.ssh/id_ed25519-cert.pub`; OpenSSH detects it by convention.

Verify with `ssh-keygen -L -f <certificate>` and test a real SSH connection.
Revoke compromised certificates in HomeCA and distribute the resulting KRL to
the applicable servers or clients.

Use short user-certificate lifetimes and automate renewal where practical. A
certificate does not replace private-key protection or server authorization.
