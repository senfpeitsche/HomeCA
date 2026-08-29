#!/usr/bin/env bash
set -euo pipefail
install -d -o homeca -g homeca -m 0750 /var/lib/homeca /var/backups/homeca /etc/homeca
install -m 0644 deploy/systemd/homeca.service /etc/systemd/system/homeca.service
systemctl daemon-reload
systemctl enable --now homeca
