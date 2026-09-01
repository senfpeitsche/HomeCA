#!/usr/bin/env bash
set -euo pipefail
apt-get update -qq
apt-get install -y -qq sudo
install -d -o homeca -g homeca -m 0750 /var/lib/homeca /var/backups/homeca /etc/homeca
install -m 0644 deploy/systemd/homeca.service /etc/systemd/system/homeca.service

# sudoers drop-in: lets the homeca user activate TLS from the web UI
mkdir -p /etc/sudoers.d
install -m 0440 deploy/sudoers/homeca-tls /etc/sudoers.d/homeca-tls

systemctl daemon-reload
systemctl enable --now homeca
