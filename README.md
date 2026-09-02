# HomeCA

A self-hosted, minimal PKI for homelabs. Manage root and issuing CAs, issue TLS/mTLS and SSH certificates, distribute trust anchors, and handle ACME flows — all from a single service with a built-in web UI.

HomeCA is designed for people who run Proxmox, OPNsense, UniFi, HAProxy, IIS, Synology, network switches, and similar infrastructure and want to get rid of certificate warnings without the complexity of enterprise PKI tools.

## Features

- Root CA and Intermediate CA management (ECC P-256 or RSA 3072)
- TLS and mTLS certificate issuance with DNS and IP SANs
- SSH host and user certificate signing
- Internal ACME server for automated internal certificate provisioning
- External ACME client (Let's Encrypt, etc.) with DNS-01 via Technitium or Hetzner DNS
- 11 target system profiles: Proxmox, OPNsense, IIS/RDP, UniFi, HAProxy, Cisco, Huawei, Synology, TeamCity, Home Assistant, generic TLS
- Export formats: PEM, key, chain, fullchain, bundle (HAProxy), PFX with issuing CA, and complete deployment-package ZIP downloads
- CRL generation and HTTP distribution with CDP extension in issued certificates
- Automatic renewal via background service
- Encrypted backups (AES-256-GCM)
- Audit logging
- Blazor Server UI with MudBlazor (German and English)
- OpenAPI documentation at `/openapi/v1.json`
- Unauthenticated Root-CA download for trust distribution; issuing certificates are delivered with certificate exports, not installed as trust anchors

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- `openssh-client` (for SSH certificate signing)

### Build and Run

```bash
git clone https://github.com/senfpeitsche/HomeCA.git
cd HomeCA
dotnet build
dotnet run --project src/HomeCA.Service
```

The UI opens at `http://localhost:5152`. In development mode, log in with `admin` / `foobar`.

### Run Tests

```bash
dotnet test
```

34 tests covering CA management, certificate issuance (ECC + RSA), CRL generation, certificate exports, security (password hashing, rate limiting), and backup/restore.

## Architecture

```
HomeCA.Service/
  Acme/              Internal ACME server + external ACME client (Certes)
  Automation/         Renewal plans + background renewal service
  Components/         Blazor Server UI (MudBlazor), localization
  Connectors/         DNS provider integrations (Technitium, Hetzner)
  Deployments/        Deployment package generation with profile snapshots
  Domains/            Domain/zone registry
  Infrastructure/     Storage abstraction, backup/restore, configuration
  Operations/         Certificate expiry warnings
  Pki/                CA management, TLS issuance, SSH issuance, CRL
  Profiles/           Target system profile registry
  Revocation/         Revocation registry
  Security/           Authentication, session management, rate limiting
```

All state is file-based (JSON + PFX/PEM files) under a configurable root path. No external database required.

## Configuration

`appsettings.json`:

```json
{
  "Storage": {
    "RootPath": "/var/lib/homeca",
    "BackupPath": "/var/backups/homeca",
    "BackupKeyPath": "/etc/homeca/backup.key",
    "PublicUrl": "http://homeca.int.example.org:5080"
  }
}
```

`PublicUrl` is used to embed CRL Distribution Points in issued certificates. Set this to the URL where your HomeCA instance is reachable on your network.

## Data Layout

| Directory | Purpose |
|-----------|---------|
| `authorities/` | CA certificates and key material |
| `certificates/` | Issued certificate records (PFX) |
| `exports/` | Immutable deployment snapshots: PEM/key/chain/fullchain/bundle plus profile snapshot, README, checksums and renewal script; downloadable as ZIP |
| `external-certificates/` | Certificates from external ACME CAs |
| `profiles/` | Target system profile snapshots |
| `crl/` | Certificate revocation lists |
| `audit/` | Append-only audit events (NDJSON) |
| `state/` | Application state (sessions, connectors, domains, etc.) |

## Deployment

HomeCA runs as a systemd service in a Debian-based LXC container. See:

- [docs/LXC-SETUP.md](docs/LXC-SETUP.md) — Full Proxmox LXC setup guide
- [docs/SSL-USAGE.md](docs/SSL-USAGE.md) — Issuing and deploying TLS certificates
- [docs/SSH-USAGE.md](docs/SSH-USAGE.md) — Issuing and deploying SSH certificates
- [docs/TRUST-INSTALLATION.md](docs/TRUST-INSTALLATION.md) — Installing the Root CA on clients and devices
- [docs/ACME-SETUP.md](docs/ACME-SETUP.md) — ACME configuration
- [docs/OPERATIONS.md](docs/OPERATIONS.md) — Day-to-day operations
- [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md) — Security boundaries and operating assumptions
- [docs/EMERGENCY-RUNBOOK.md](docs/EMERGENCY-RUNBOOK.md) — Recovery and compromise response
- [docs/LIFECYCLE.md](docs/LIFECYCLE.md) — Certificate lifecycle routine
- [docs/REVERSE-PROXY.md](docs/REVERSE-PROXY.md) — LAN access through Caddy or nginx

By default the production service listens on LAN port `5080`, so that a homelab administrator can reach the PKI from the network. Do not expose that port to the Internet. Activate TLS in the web UI before normal operation, limit access with the host firewall or a reverse proxy, and use VPN for remote administration. TLS activation switches the service to the configured HTTPS listener (normally `https://<hostname>:5443`) and redirects the browser to that configured URL. A reverse proxy (HAProxy, nginx, Caddy) may instead terminate TLS on port 443; see [docs/REVERSE-PROXY.md](docs/REVERSE-PROXY.md).

## API

The full API is documented via OpenAPI at `/openapi/v1.json` when the service is running.

Key public (unauthenticated) endpoints:
- `GET /health` — Health check
- `GET /api/v1/trust-anchor` — Root CA metadata and SHA-256 fingerprint
- `GET /api/v1/trust-anchor/pem` — Root CA certificate download (PEM)
- `GET /api/v1/trust-anchor/der` — Root CA certificate download (DER/CER)
- `GET /api/v1/crl/latest` — Current CRL download

All management endpoints require a Bearer token obtained via `POST /api/v1/login`.

Certificate exports also provide `GET /api/v1/certificates/{id}/export/package`, which downloads the complete deployment snapshot as a ZIP. It contains the private key and is intentionally only available to authenticated users.

## Backup Format

Encrypted backups use the `HCAB1` format: a ZIP payload encrypted with AES-256-GCM. The 32-byte encryption key resides at the configured `BackupKeyPath` and must be backed up separately.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup and guidelines.

## License

[MIT](LICENSE)
