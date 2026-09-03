# LAN-Zugriff ueber Reverse Proxy

LAN-Zugriff auf HomeCA ist ein vorgesehener Betriebsmodus. Ein Reverse Proxy bietet dabei eine feste HTTPS-Adresse, kann Zugriff auf Admin- und Servernetze begrenzen und haelt HomeCA selbst auf einem internen Port.

## Empfohlene Topologie

`Admin-/Servernetz -> Reverse Proxy (HTTPS) -> HomeCA (LAN oder localhost)`

HomeCA niemals direkt aus dem Internet veroeffentlichen. Fernadministration erfolgt ueber VPN. Der Proxy darf nur aus den benoetigten Netzen erreichbar sein; die HomeCA-Firewall sollte ausschliesslich den Proxy und bekannte ACME-Clients zulassen, wenn diese nicht ueber den Proxy gehen.

## Voraussetzungen

- Ein DNS-Name, etwa `pki.home.arpa`, zeigt auf den Reverse Proxy.
- Das Root-Zertifikat ist auf dem Admin-Client installiert, bevor HomeCA sein eigenes TLS-Zertifikat verwendet.
- `Storage:PublicUrl` zeigt auf die tatsaechliche HTTPS-Adresse, damit CRL-Links erreichbar bleiben.
- Der Proxy laeuft auf demselben Host wie HomeCA und uebermittelt `X-Forwarded-For` sowie `X-Forwarded-Proto`; keine Verwaltungsroute darf anonym nach aussen freigegeben werden. HomeCA vertraut diesen Headern ausschliesslich von `127.0.0.1` bzw. `::1`.

## Caddy-Beispiel

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

## nginx-Beispiel

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

## Pruefliste

1. `https://pki.home.arpa/health` aus einem erlaubten Netz pruefen.
2. Aus einem nicht erlaubten Netz pruefen, dass der Proxy ablehnt.
3. Anmeldung, Root-Download, CRL-Download und eine ACME-Ausstellung testen.
4. Nach jeder Zertifikatsrotation die ausgelieferte Kette mit `openssl s_client -connect pki.home.arpa:443 -showcerts` pruefen.
5. Proxy-Konfiguration und Firewall-Regeln zusammen mit dem HomeCA-Backup dokumentieren.
