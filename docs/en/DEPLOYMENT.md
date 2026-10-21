# Deploy certificates automatically

A **deployment target** follows a certificate through all renewals. When HomeCA issues a successor – automatically through a renewal plan or via **Renew now** – the target points to the new certificate right away. A small agent on the device fetches the files and runs the profile script.

## Flow

1. Issue a certificate (the renewal plan is on by default).
2. Under **Deployment**, add a target: name, certificate, optionally host and port for the check, and a webhook.
3. HomeCA shows a token and the setup commands **once**. Run them as root on the device.
4. The agent runs hourly via cron. It loads the manifest, compares the fingerprint with the installed certificate, and only acts on a change.

## What the agent does

- `GET /api/v1/deploy/{id}/manifest` returns certificate ID, SHA-256 fingerprint, expiry and the available files.
- `GET /api/v1/deploy/{id}/files/{file}` returns `certificate.pem`, `key.pem`, `chain.pem`, `fullchain.pem`, `bundle.pem` and the profile script (`install.sh`, `install.ps1` or `device-commands.txt`).
- Every request authenticates with `Authorization: Bearer <target token>`. The token is valid for this one target only, not for the management API.
- Files land in a temporary directory first. With `openssl` available, the agent verifies the fingerprint. The directory is then moved atomically to `/var/lib/homeca-agent/<id>/current`; the previous state stays under `previous`.
- If `install.sh` is present, the agent runs it in the new directory. `RUN_SCRIPT=0` only downloads the files.
- If the HomeCA root CA is not in the system store, `CA_FILE` points to it.

`--force` reinstalls the current certificate even without a change.

## Webhook

With a webhook configured, HomeCA sends a JSON POST after each renewal:

```json
{ "event": "certificate.renewed", "targetId": "…", "targetName": "pve01", "certificateId": "…", "fingerprint": "…", "manifestUrl": "https://homeca…/api/v1/deploy/…/manifest" }
```

The payload carries no secrets; the receiver fetches the files with its token like the agent. An unreachable webhook does not stop the renewal.

## Checking served certificates

With host and port, HomeCA connects to the device over TLS and compares the served certificate:

| Status | Meaning |
| --- | --- |
| Current | The device serves the target's current certificate. |
| Outdated certificate | The device serves an older HomeCA certificate – usually the agent did not run or the service was not reloaded. |
| Different certificate | The device serves a certificate HomeCA does not know. |
| Unreachable | No TLS connection was possible. |

The check runs on demand and every six hours in the background. When a target changes to a state other than "Current", a notification is sent and the overview lists it under **Needs attention**.

## Managing tokens

- **New token** invalidates the old one immediately. Run the setup commands on the device again, or replace only the token file.
- Token files are stored with mode `600` at `/etc/homeca-agent/<id>.token`.
- Whoever holds a token can fetch the target's private key. Treat it like the key itself.

## Devices without a shell

For devices with a web interface or their own CLI (FRITZ!Box, OPNsense, switches), the package contains `device-commands.txt` with the steps. For devices that create their own key, **Sign CSR** is the better route; HomeCA cannot renew those certificates on its own.
