# Running HomeCA in a container

Besides the LXC installation, HomeCA runs as a container. The image contains .NET, the OpenSSH client for SSH certificates and a start script that creates the CA and backup keys on first start.

## Start

```bash
docker compose up -d --build
docker compose logs homeca
```

Then open `http://<host>:5080` and sign in as `admin` / `admin`; HomeCA asks for a new password right away. Set `Storage__PublicUrl` in `docker-compose.yml` to the address clients use to reach HomeCA – it ends up in every certificate as the CRL location.

## Volumes

| Path in the container | Contents |
| --- | --- |
| `/data` | CAs, certificates, exports, state |
| `/config` | `ca.key`, `backup.key` and runtime configuration |
| `/backups` | Encrypted backups |

**Back up `/config` separately.** Without `ca.key` the CA keys cannot be opened, without `backup.key` no backup can be decrypted. For scheduled backups to a NAS, mount a share as an extra volume and enter its path inside the container as the second directory.

## TLS

The TLS activation in the web interface relies on systemd and does not work in the container. Put a reverse proxy (Caddy, Traefik, nginx) in front of the container, see [REVERSE-PROXY.md](REVERSE-PROXY.md). Do not expose port `5080` to the Internet.

## Updating

```bash
git pull
docker compose up -d --build
```

Data lives in the volumes and is kept. Create a backup before every update.
