# HomeCA in a Proxmox Debian LXC

This guide sets up a single HomeCA instance in a Debian 13 LXC. HomeCA is a private CA: protect the container like a core administrative system and do not expose it directly to the Internet.

## Quick start — versioned installation

Open the **Proxmox shell** (node → Shell in the web UI), pick a release tag and run its bootstrap script:

```bash
export HOMECA_VERSION=v0.7.0
curl -fsSLO "https://github.com/senfpeitsche/HomeCA/releases/download/${HOMECA_VERSION}/homeca-lxc.sh"
bash ./homeca-lxc.sh
```

This applies from `v0.7.0` on. The script uses the engine of the [Proxmox VE Helper-Scripts](https://community-scripts.org/) (`community-scripts/core`, pinned to one commit) and runs as follows:

1. **Diagnostics:** On the first run on a host, the engine asks whether it may send anonymous installation data (RAM, CPU, container name, no content) to community-scripts. The answer is stored in `/usr/local/community-scripts/diagnostics`.
2. **Settings:** Choose **Default Install** for the defaults or **Advanced Install** for container ID, hostname, resources, network (DHCP or static, gateway, VLAN, MTU), SSH keys and further options.
3. **Storage:** The engine only offers storages that can hold container disks or templates, such as `local-lvm`, `local-zfs` or a custom pool. If only one storage fits, it uses it without asking.
4. **Template:** The engine downloads the current Debian 13 template if it is not cached yet.
5. **Installation:** Inside the unprivileged container it installs .NET 10, HomeCA with the `homeca` service account and the systemd service. All files come from the script's release and are verified with SHA-256.

At the end the script shows the address `http://<IP>:5080`. This HTTP access is meant for the initial setup only: activate TLS afterwards and restrict access to admin and server networks.

### Adjust defaults

Environment variables preset the defaults; the dialogs take them over:

```bash
HOMECA_CTID=110 HOMECA_HOSTNAME=pki HOMECA_RAM=2048 bash ./homeca-lxc.sh
```

| Variable | Default | Description |
| --- | --- | --- |
| `HOMECA_CTID` | next free ID | Container ID (`var_ctid`) |
| `HOMECA_HOSTNAME` | `homeca` | LXC hostname (`var_hostname`) |
| `HOMECA_DISK` | `8` | Root disk in GiB (`var_disk`) |
| `HOMECA_RAM` | `1024` | Memory in MiB (`var_ram`) |
| `HOMECA_CORES` | `1` | CPU cores (`var_cpu`) |
| `HOMECA_STORAGE` | asked | Storage for the container disk (`var_container_storage`) |
| `HOMECA_BRIDGE` | `vmbr0` | Network bridge (`var_brg`) |
| `HOMECA_NET` | `dhcp` | `dhcp` or CIDR, e.g. `10.0.0.50/24,gw=10.0.0.1` (`var_net`, `var_gateway`) |
| `HOMECA_VERSION` | the script's release | Release tag (e.g. `v0.7.0`) |

Every other engine setting works as a `var_*` variable as well, for example `var_template_storage`, `var_vlan` or `var_ssh=yes`.

An existing Debian 12 container stays on Debian 12; `homeca-update.sh` keeps working there.

### Set up the container only (without the Proxmox host script)

If a Debian 12 or Debian 13 LXC already exists, run the installation script directly inside the container as `root`:

```bash
export HOMECA_VERSION=v0.7.0
curl -fsSLO "https://github.com/senfpeitsche/HomeCA/releases/download/${HOMECA_VERSION}/homeca-install.sh"
bash ./homeca-install.sh
```

### Troubleshooting

| Message | Cause and fix |
| --- | --- |
| `400 Parameter verification failed. template: no such template` | `homeca-lxc.sh` before `v0.7.0` with a hard-coded template version. Use the script from `v0.7.0` or newer. |
| `storage 'local-lvm' does not exist` | `homeca-lxc.sh` before `v0.7.0` with a fixed storage. Use the script from `v0.7.0` or newer, or set `HOMECA_STORAGE`. `pvesm status --content rootdir` lists suitable storages. |
| `Could not fetch install/homeca-install.sh` | The selected release tag is older than `v0.7.0`, or GitHub is unreachable. |

An aborted run can leave a half-configured container. Check its ID with `pct list` and remove only that container with `pct destroy <ID>`.

## Update

### Inside the container:

```bash
/opt/homeca/homeca-update.sh
```

### From the Proxmox host (adjust the container ID):

```bash
pct exec 100 -- /opt/homeca/homeca-update.sh
```

### Install a specific version:

```bash
HOMECA_VERSION=v0.7.0 /opt/homeca/homeca-update.sh
```

### Disable TLS again (back to HTTP)

Run inside the container as `root`:

```bash
bash /opt/homeca/homeca-deactivate-tls.sh
```

The script removes only the active systemd TLS override, then checks `http://127.0.0.1:5080/health` and keeps the override as `/etc/homeca/tls.conf.disabled`. Certificate and TLS configuration remain for a later reactivation.

### Activate TLS from the web UI

Open **Settings > TLS for the HomeCA interface**, enter the DNS name and issue the certificate. **Activate TLS and restart now** writes the systemd override and switches HomeCA to HTTPS. After the restart the browser moves to the configured target address (usually `https://<hostname>:5443`).

Install the root CA in the browser or operating system first; see [TRUST-INSTALLATION.md](TRUST-INSTALLATION.md). If the switch fails, run `bash /opt/homeca/homeca-deactivate-tls.sh` inside the container to return safely to HTTP.

### What the update script does:

1. Checks whether the target version is already installed
2. Updates system packages
3. Creates a backup (data + configuration) under `/var/backups/homeca/`
4. Stops the service and keeps the previous version for rollback
5. Downloads and deploys the new release
6. Updates the systemd unit if it changed in the release
7. Starts the service and checks the health endpoint
8. Rolls back automatically if the health check fails

## Initial setup after installation

1. Run the administrator setup through the HomeCA LAN address (`http://<homeca-host>:5080`), and only from a trusted admin network.
2. Initialize the root and issuing CA.
3. Create at least one internal issuance zone and issue a test certificate.
4. Check the certificate chain, CRL, backup and DNS connector permissions.
5. Activate TLS and restrict LAN access with a host firewall or a TLS-terminating reverse proxy. A ready-to-use reference is in [REVERSE-PROXY.md](REVERSE-PROXY.md).

## Backup keys

The installation creates two separate 32-byte keys: `/etc/homeca/backup.key` for backups and `/etc/homeca/ca.key` to protect the CA PFX files. **Keep a copy of both keys outside the container.** Without the backup key, HCAB1 backups cannot be restored; without the CA key, the CA private keys cannot be loaded.

```bash
# Copy the keys out of the container (on the Proxmox host)
pct pull 100 /etc/homeca/backup.key ./homeca-backup.key
pct pull 100 /etc/homeca/ca.key ./homeca-ca.key
```

## Manual installation (reference)

If you do not want to use the automatic scripts, this section documents the individual steps.

### 1. Create the container in Proxmox

Create an **unprivileged** LXC with the Debian 13 template in the Proxmox web UI (Debian 12 works as well).

| Setting | Recommendation |
| --- | --- |
| CPU | 1 vCPU, 2 vCPU for many issuances |
| Memory | 1 GiB, at least 512 MiB |
| System disk | at least 8 GiB, an additional backup target is recommended |
| Network | fixed DHCP reservation or static internal address |
| Features | no nested virtualization; `nesting` only if needed for something else |

Do not give the container a public port. The bundled systemd unit serves HomeCA on port `5080` in the LAN. For permanent operation, activate TLS and restrict access to admin and server networks with a host firewall or reverse proxy.

### 2. Prepare the base system

```bash
apt update
apt full-upgrade
apt install --yes ca-certificates curl openssh-client
```

Install the `aspnetcore-runtime-10.0` package from the Microsoft package repository for your Debian version (`https://packages.microsoft.com/config/debian/<12|13>/packages-microsoft-prod.deb`). Check with `dotnet --info`.

Create the service account:

```bash
adduser --system --group --home /var/lib/homeca --shell /usr/sbin/nologin homeca
```

### 3. Deploy the application and keys

```bash
install -d -o root -g root -m 0755 /opt/homeca
# Copy the release artifacts to /opt/homeca
install -d -o homeca -g homeca -m 0750 /var/lib/homeca /var/backups/homeca /etc/homeca
umask 077
head -c 32 /dev/urandom > /etc/homeca/backup.key
chown homeca:homeca /etc/homeca/backup.key
chmod 0600 /etc/homeca/backup.key
head -c 32 /dev/urandom > /etc/homeca/ca.key
chown homeca:homeca /etc/homeca/ca.key
chmod 0600 /etc/homeca/ca.key
```

### 4. Enable the systemd service

```bash
cp deploy/systemd/homeca.service /etc/systemd/system/homeca.service
systemctl daemon-reload
systemctl enable --now homeca
curl --fail http://127.0.0.1:5080/health
```

### 5. Manual update

```bash
systemctl stop homeca
# Replace the release in /opt/homeca
systemctl start homeca
curl --fail http://127.0.0.1:5080/health
```

For restore and regular checks see [OPERATIONS.md](OPERATIONS.md).

## Script overview

| Script | Purpose | Where it runs |
| --- | --- | --- |
| `deploy/proxmox/ct/homeca.sh` (release: `homeca-lxc.sh`) | Create the LXC through community-scripts/core and start the installation | Proxmox shell |
| `deploy/proxmox/install/homeca-install.sh` | Prepare the container and run `homeca-install.sh` from the release | In the LXC, called by the engine |
| `deploy/scripts/homeca-install.sh` | Install HomeCA in the container | In the LXC (root) |
| `deploy/scripts/homeca-update.sh` | Update HomeCA with backup + rollback | In the LXC (root) or via `pct push`/`pct exec` |
| `deploy/scripts/homeca-activate-tls.sh` | Activate the TLS listener from the prepared TLS configuration | In the LXC (root), usually triggered from the web UI |
| `deploy/scripts/homeca-deactivate-tls.sh` | Remove the TLS override and restore HTTP | In the LXC (root) |
