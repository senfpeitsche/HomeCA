# LAN Access Through a Reverse Proxy

LAN access to HomeCA is a supported operating mode. A reverse proxy gives it a stable HTTPS address, can restrict access to administration and server networks, and keeps HomeCA itself on an internal port.

## Recommended topology

`Administration/server network -> Reverse proxy (HTTPS) -> HomeCA (LAN or localhost)`

Never publish HomeCA directly on the Internet. Use a VPN for remote administration. The proxy must be reachable only from the required networks; the HomeCA firewall should allow only the proxy and known ACME clients when they do not use the proxy.

## Prerequisites

- A DNS name such as `pki.home.arpa` points to the reverse proxy.
- The root certificate is installed on the administration client before HomeCA uses its own TLS certificate.
- `Storage:PublicUrl` points to the real HTTPS address so CRL links remain reachable.
- The proxy runs on the same host as HomeCA and forwards `X-Forwarded-For` and `X-Forwarded-Proto`; no management route may be exposed anonymously. HomeCA trusts those headers only from `127.0.0.1` and `::1`.

## Caddy example

```caddy
pki.home.arpa {
    tls /etc/caddy/certs/pki-fullchain.pem /etc/caddy/certs/pki-key.pem
    @allowed remote_ip 192.168.10.0/24 192.168.20.0/24
    handle @allowed {
        reverse_proxy http://127.0.0.1:5080
    }
    respond "Forbidden" 403
}
```

## nginx example

```nginx
server {
    listen 443 ssl;
    server_name pki.home.arpa;
    ssl_certificate     /etc/nginx/ssl/pki-fullchain.pem;
    ssl_certificate_key /etc/nginx/ssl/pki-key.pem;

    allow 192.168.10.0/24;
    allow 192.168.20.0/24;
    deny all;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

## Checklist

1. Test `https://pki.home.arpa/health` from an allowed network.
2. Confirm the proxy rejects a request from a non-allowed network.
3. Test sign-in, root download, CRL download, and an ACME issuance.
4. After every certificate rotation, inspect the served chain with `openssl s_client -connect pki.home.arpa:443 -showcerts`.
5. Document proxy configuration and firewall rules together with the HomeCA backup.
