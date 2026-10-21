# Deploy certificates automatically

A **deployment target** follows a certificate through all renewals. When HomeCA issues a successor – automatically through a renewal plan or via **Renew now** – the target points to the new certificate right away. A small agent on the device fetches the files and runs the profile script.

## Setup over SSH

Under **Deployment > Set up over SSH**, HomeCA does the setup on Linux devices itself:

1. Enter host, port, user and a password or private key. A user other than root needs `sudo`; the sudo password is only needed when it differs from the SSH password.
2. On first contact HomeCA shows the SHA-256 fingerprint of the host key and only signs in after you confirm it. Compare it on the device's console with `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`.
3. HomeCA only reads the system: host name, FQDN, addresses, privileges, systemd or cron, trust store and running services (for example Proxmox, nginx, Caddy, Traefik, Jellyfin). From these it suggests a profile, names and a matching existing certificate; if none exists, you can issue one right in the wizard.
4. **Install agent** creates the target (or renews the token when setting it up again), writes the token, the agent and an hourly systemd timer or cron entry, optionally adds the root CA to the system trust store and runs the agent once. Output and exit code appear directly in the interface.

Password, key, passphrase and token are never stored or logged. The target keeps only host, port, user name and the confirmed host key. If the host key differs later, HomeCA stops before signing in; after reinstalling the device you accept the new key explicitly.

If the device cannot reach HomeCA at the address in your browser (for example `localhost`), enter the right address under **HomeCA URL as seen from the device**. If the system does not trust HomeCA's TLS certificate, the agent uses a copy of the root CA automatically (`CA_FILE` in `/etc/homeca-agent/<id>.env`).

During the setup the wizard shows every line under **Output so far**, with the time, as soon as the device prints it. The same lines are in HomeCA's service log (`journalctl -u homeca`, without passwords and tokens). The setup may take ten minutes; if it does not finish in time, HomeCA shows the output up to that point.

## Windows

Windows gets a PowerShell agent (`agent.ps1`, Windows PowerShell 5.1 or PowerShell 7). It runs hourly as the scheduled task `\HomeCA\Agent <id>` under SYSTEM, fetches the files like the shell agent and runs `install.ps1`. It also gets `certificate.pfx` with certificate, key and issuing CA; the PFX is protected with the target token, and `install.ps1` receives the password in `HOMECA_PFX_PASSWORD`.

- **Setup over SSH:** If OpenSSH is enabled on the server (built in since Windows 10 1809 and Server 2019), **Set up over SSH** detects Windows automatically, with cmd or PowerShell as the default shell and even with Git Bash in the PATH. The user must be an administrator. HomeCA creates `%ProgramData%\HomeCA\Agent` with access for SYSTEM and Administrators only, writes token and agent, adds the root CA to `LocalMachine\Root` (on by default, can be turned off), registers the task and runs the agent once. Push is not available for Windows. Logs are in `%ProgramData%\HomeCA\Logs`: `setup-<id>-<timestamp>.log` for every setup, `agent-<id>.log` for every agent run (moved to `.old` at 1 MB).
- **Without OpenSSH:** After you add a target, HomeCA shows PowerShell commands with the token under **Windows**. Run them in an elevated PowerShell, locally or in your own remoting session (`Enter-PSSession`, so WinRM works too).
- **Trust:** If Windows does not trust HomeCA's TLS certificate, the agent validates the chain against its copy of the root CA (`caFile` in `%ProgramData%\HomeCA\Agent\<id>.settings.json`).
- **Profiles:** `iis` imports the certificate into `LocalMachine\My` and moves every HTTPS binding whose certificate has the same subject to it. Bind a new site once in IIS Manager. `rdp` imports the certificate, gives the Remote Desktop service (`NETWORK SERVICE`) read access to the key and points the `RDP-Tcp` listener to it on every renewal; `restore.ps1` sets the previous thumbprint back. The guided setup suggests `rdp` when Remote Desktop is allowed. `windows-certstore` only imports, for example for a WinRM HTTPS listener.
- **Backups:** Before every change HomeCA saves the affected files, the scheduled task (`task.xml`), newly imported certificates and the previous certificate of every IIS binding under `%ProgramData%\HomeCA\Backups\<target>\<timestamp>-<reason>\`. `restore.ps1` in an elevated PowerShell restores the previous state, newest backup first. Custom profiles use `Backup-HomeCAFile <path>`, `Add-HomeCARestoreStep '<command>'` and `Import-HomeCACertificate certificate.pfx`.

HomeCA does not offer a direct setup over WinRM: there is no maintained client for it on Linux, and without Kerberos or an HTTPS listener there is no server authentication like the SSH host key.

## Push renewals immediately

With **Push renewals immediately** (the default) the setup adds HomeCA's own key (ECDSA P-256) to root's `authorized_keys`:

```
command="/usr/local/bin/homeca-agent-<id>",no-pty,no-port-forwarding,no-agent-forwarding,no-X11-forwarding ecdsa-sha2-nistp256 AAAA… homeca-push-<id>
```

The key can only start this target's agent: no shell, no terminal and no forwarding, whatever command the client sends. After every renewal HomeCA signs in as root with the stored host key and this key and starts the agent, so the device has the new certificate within seconds instead of up to an hour. **Push now** in the target list does the same by hand.

- Right after the setup, a test push checks that `sshd` accepts the key. If `sshd` refuses root logins (`PermitRootLogin no`), push stays off.
- If another agent run is in progress (for example the timer), the agent exits with code 75; HomeCA retries up to four times, 30 seconds apart.
- A failed push sends the **Push failed** notification. The hourly timer always stays active and still fetches the certificate.
- If the host key differs, HomeCA does not sign in and the push counts as failed.
- **Turn off push** stops pushes in HomeCA. The line on the device is removed by the setup's `restore.sh` or by setting the device up again without push. Other entries in `authorized_keys` stay untouched.
- The private key is stored encrypted with the CA key under `state/ssh-push-key.pem` and is part of the HomeCA backup.

## Backups on the device

Before every change, HomeCA saves the affected files under `/var/backups/homeca/<target>/<timestamp>-<reason>/`:

- `-setup`: everything the setup writes (token, agent, timer, cron, root CA).
- `-renewal`: everything the profile script replaces during an agent run, such as `/etc/nginx/ssl/fullchain.pem`.

Each backup contains `files.txt` and `restore.sh`. `restore.sh` copies replaced files back and removes files that did not exist before. Undo several changes in reverse order, newest backup first. The agent keeps the ten newest backups per target (`KEEP_BACKUPS`).

In profile scripts, `cp` and `install` save their destination automatically. If a custom profile writes in another way, for example with `>` or `openssl -out`, call `homeca_backup /path/to/file` first. Without the agent, backups go to `/var/backups/homeca/manual/`.

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
