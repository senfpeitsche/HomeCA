#!/usr/bin/env bash

# Copyright (c) 2021-2026 community-scripts ORG
# Author: senfpeitsche
# License: MIT | https://github.com/community-scripts/ProxmoxVED/raw/main/LICENSE
# Source: https://github.com/senfpeitsche/HomeCA

source /dev/stdin <<<"$FUNCTIONS_FILE_PATH"
color
verb_ip6
catch_errors
setting_up_container
network_check
update_os

msg_info "Installing Dependencies"
$STD apt install -y openssh-client
msg_ok "Installed Dependencies"

DOTNET_VERSION="10" DOTNET_TYPE="aspnetcore" setup_dotnet

fetch_and_deploy_gh_release "homeca" "senfpeitsche/HomeCA" "prebuild" "latest" "/opt/homeca" "homeca-linux-x64.tar.gz"

msg_info "Generating Encryption Keys"
# The drop-in directory must exist up front: it is listed in ReadWritePaths and
# systemd refuses to start the unit if a path there is missing.
mkdir -p /etc/homeca /var/lib/homeca /var/backups/homeca /etc/systemd/system/homeca.service.d
# HomeCA reads both keys at startup and does not create them itself.
# backup.key wraps encrypted backups, ca.key protects the CA private keys at rest.
(
  umask 077
  head -c 32 /dev/urandom >/etc/homeca/backup.key
  head -c 32 /dev/urandom >/etc/homeca/ca.key
)
msg_ok "Generated Encryption Keys"

msg_info "Creating Service"
cat <<EOF >/etc/systemd/system/homeca.service
[Unit]
Description=HomeCA private certificate authority
After=network-online.target
Wants=network-online.target

[Service]
Type=exec
User=root
WorkingDirectory=/opt/homeca
ExecStart=/usr/bin/dotnet /opt/homeca/HomeCA.Service.dll
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5080
# ProtectHome makes /root unreadable, and ASP.NET Core keeps its Data Protection
# keyring under \$HOME — left at /root the keys would be ephemeral.
Environment=HOME=/var/lib/homeca
Environment=Storage__RootPath=/var/lib/homeca
Environment=Storage__BackupPath=/var/backups/homeca
Environment=Storage__BackupKeyPath=/etc/homeca/backup.key
Environment=Storage__CaKeyPath=/etc/homeca/ca.key
Environment=Storage__ConfigurationPath=/etc/homeca
Environment=Storage__PublicUrl=http://${LOCAL_IP}:5080
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
# Enabling TLS from the web UI writes a drop-in for this unit.
ReadWritePaths=/var/lib/homeca /var/backups/homeca /etc/homeca /etc/systemd/system/homeca.service.d

[Install]
WantedBy=multi-user.target
EOF
systemctl enable -q --now homeca
msg_ok "Created Service"

motd_ssh
customize
cleanup_lxc
