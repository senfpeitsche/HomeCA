# HomeCA in a Proxmox Debian LXC

## Quick start

Use the versioned installation script from a Proxmox host. It creates or
prepares a Debian LXC, installs the selected HomeCA release under
`/opt/homeca`, and enables the `homeca` systemd service. Adjust container ID,
hostname, storage, CPU and memory to your environment before running it.

After installation, open the HomeCA URL shown by the script, complete the
initial setup, and store the recovery/backup key outside the container.

## Update

Run the update script inside the container or from the Proxmox host with the
target container ID. It downloads and installs the selected release, preserves
the data directory and restarts the service. Verify `/health` afterwards.

To return from TLS to HTTP, use the documented TLS-disable operation or update
the deployment configuration, then restart HomeCA. Do not delete certificates
or the data directory to change transport settings.

## Manual reference installation

1. Create a supported Debian LXC and enable network access.
2. Install required system packages and create the HomeCA service account.
3. Copy the release artifacts and service files to `/opt/homeca`.
4. Place data and backup material on persistent storage with restrictive
   permissions.
5. Enable and start `homeca` with systemd, then test `/health`.

Keep a verified backup before upgrades. The backup key is required for restore;
copy it from the container to an offline, protected location.
