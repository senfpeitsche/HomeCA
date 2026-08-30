# HomeCA deployment foundation

HomeCA is an ASP.NET Core service targeting .NET 10. It is designed for a Debian-based Proxmox LXC and stores durable state below `/var/lib/homeca`.

## Layout

- `authorities`: CA certificates and key material
- `certificates`: issued certificate records
- `exports`: deployment packages
- `profiles`: immutable profile snapshots
- `crl`: certificate-revocation lists
- `audit`: append-only audit events
- `state/homeca-state.json`: embedded local state metadata

Encrypted backups are stored separately in `/var/backups/homeca`. The `HCAB1` format is a ZIP payload encrypted with AES-256-GCM; the 32-byte key resides at `/etc/homeca/backup.key` and must be backed up separately. The archive header is `HCAB1`, followed by a 12-byte nonce, 16-byte authentication tag, and ciphertext.

`deploy/systemd/homeca.service` is the production service definition. Before first production start, create the `homeca` system user, the data directories, and an owner-readable-only 32-byte backup key.

For a complete Proxmox Debian LXC setup, see [docs/LXC-SETUP.md](docs/LXC-SETUP.md).

## Lokales Debugging in Rider

Ein Start des Profils `http` von `HomeCA.Service` öffnet die integrierte MudBlazor-Oberfläche unter `http://localhost:5152`. Es ist kein separater Node-, Vite- oder SPA-Devserver erforderlich.

Im Entwicklungsprofil ist die Anmeldung mit `admin` / `foobar` verfügbar. Diese Zugangsdaten gelten ausschließlich bei `ASPNETCORE_ENVIRONMENT=Development` und werden in der Produktion nicht akzeptiert.
