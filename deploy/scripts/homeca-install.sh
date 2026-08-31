#!/usr/bin/env bash
# HomeCA — in-container install script
#
# Called automatically by homeca-lxc.sh, or run manually inside a Debian 12 LXC.
# For private repo, download via API first:
#   GITHUB_TOKEN=ghp_… bash <(curl -fsSL \
#     -H 'Authorization: token ghp_…' -H 'Accept: application/vnd.github.raw' \
#     'https://api.github.com/repos/senfpeitsche/HomeCA/contents/deploy/scripts/homeca-install.sh?ref=main')
#
# Environment overrides:
#   GITHUB_TOKEN      — GitHub PAT for private repo access (required while repo is private)
#   HOMECA_VERSION    — release tag (default: latest)

set -euo pipefail

VERSION="${HOMECA_VERSION:-latest}"
GH_REPO="senfpeitsche/HomeCA"

APP_DIR="/opt/homeca"
DATA_DIR="/var/lib/homeca"
BACKUP_DIR="/var/backups/homeca"
CONFIG_DIR="/etc/homeca"
SERVICE_FILE="/etc/systemd/system/homeca.service"

# ── Auth + download helpers for private repo ────────────────────────
GH_AUTH_HEADER=()
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  GH_AUTH_HEADER=(-H "Authorization: token ${GITHUB_TOKEN}")
fi

# Download a release asset (works for both private and public repos)
gh_download_release() {
  local version="$1" asset="$2" dest="$3"
  if [[ -n "${GITHUB_TOKEN:-}" ]]; then
    # Private repo: use API to find asset URL, then download with octet-stream accept
    local release_url
    if [[ "$version" == "latest" ]]; then
      release_url="https://api.github.com/repos/${GH_REPO}/releases/latest"
    else
      release_url="https://api.github.com/repos/${GH_REPO}/releases/tags/${version}"
    fi
    local asset_url
    asset_url=$(curl -fsSL "${GH_AUTH_HEADER[@]}" "$release_url" \
      | grep -o "\"browser_download_url\": *\"[^\"]*${asset}\"" \
      | head -1 | cut -d'"' -f4)
    if [[ -z "$asset_url" ]]; then
      echo "ERROR: Asset '${asset}' not found in release ${version}" >&2
      return 1
    fi
    # For private repos, browser_download_url needs auth + redirect follow
    curl -fsSL "${GH_AUTH_HEADER[@]}" -H "Accept: application/octet-stream" \
      -o "$dest" -L "$asset_url"
  else
    # Public repo: direct download
    local base="https://github.com/${GH_REPO}/releases"
    if [[ "$version" == "latest" ]]; then
      curl -fsSL -o "$dest" "${base}/latest/download/${asset}"
    else
      curl -fsSL -o "$dest" "${base}/download/${version}/${asset}"
    fi
  fi
}

# Download a raw file from the repo
gh_raw() {
  local path="$1" dest="${2:--}"
  if [[ -n "${GITHUB_TOKEN:-}" ]]; then
    curl -fsSL "${GH_AUTH_HEADER[@]}" \
      -H "Accept: application/vnd.github.raw" \
      -o "$dest" \
      "https://api.github.com/repos/${GH_REPO}/contents/${path}?ref=main"
  else
    curl -fsSL -o "$dest" \
      "https://raw.githubusercontent.com/${GH_REPO}/main/${path}"
  fi
}

# Resolve the latest release tag
gh_resolve_latest_tag() {
  if [[ -n "${GITHUB_TOKEN:-}" ]]; then
    curl -fsSL "${GH_AUTH_HEADER[@]}" \
      "https://api.github.com/repos/${GH_REPO}/releases/latest" \
      | grep '"tag_name"' | head -1 | cut -d'"' -f4
  else
    curl -fsSI "https://github.com/${GH_REPO}/releases/latest" 2>/dev/null \
      | grep -i '^location:' | grep -oP 'tag/\K[^\s\r]+' || echo "latest"
  fi
}

# ── Colors / helpers ────────────────────────────────────────────────
BL='\033[36m' GN='\033[32m' RD='\033[31m' YW='\033[33m' CL='\033[0m'
msg_info()  { echo -e " ${BL}[INFO]${CL}  $1"; }
msg_ok()    { echo -e " ${GN}[OK]${CL}    $1"; }
msg_error() { echo -e " ${RD}[ERROR]${CL} $1"; exit 1; }

# ── Pre-flight ──────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
  msg_error "This script must run as root."
fi

if [[ -f "${APP_DIR}/HomeCA.Service.dll" ]]; then
  msg_error "HomeCA is already installed. Use homeca-update.sh to update."
fi

# ── System packages ─────────────────────────────────────────────────
msg_info "Updating system packages …"
apt-get update -qq
apt-get full-upgrade -y -qq
msg_ok "System updated"

msg_info "Installing dependencies …"
apt-get install -y -qq ca-certificates curl openssh-client apt-transport-https
msg_ok "Dependencies installed"

# ── .NET 10 Runtime ─────────────────────────────────────────────────
if ! command -v dotnet &>/dev/null; then
  msg_info "Installing .NET 10 Runtime …"
  curl -fsSL https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb
  dpkg -i /tmp/packages-microsoft-prod.deb
  rm -f /tmp/packages-microsoft-prod.deb
  apt-get update -qq
  apt-get install -y -qq aspnetcore-runtime-10.0
  msg_ok ".NET 10 Runtime installed"
else
  msg_ok ".NET Runtime already present ($(dotnet --version))"
fi

# ── Service user ────────────────────────────────────────────────────
if ! id homeca &>/dev/null; then
  msg_info "Creating service user …"
  adduser --system --group --home "$DATA_DIR" --shell /usr/sbin/nologin homeca
  msg_ok "User 'homeca' created"
else
  msg_ok "User 'homeca' already exists"
fi

# ── Directories ─────────────────────────────────────────────────────
msg_info "Setting up directories …"
install -d -o root   -g root   -m 0755 "$APP_DIR"
install -d -o homeca -g homeca -m 0750 "$DATA_DIR" "$BACKUP_DIR" "$CONFIG_DIR"
msg_ok "Directories ready"

# ── Backup encryption key ───────────────────────────────────────────
if [[ ! -f "${CONFIG_DIR}/backup.key" ]]; then
  msg_info "Generating backup encryption key …"
  umask 077
  head -c 32 /dev/urandom > "${CONFIG_DIR}/backup.key"
  chown homeca:homeca "${CONFIG_DIR}/backup.key"
  chmod 0600 "${CONFIG_DIR}/backup.key"
  msg_ok "Backup key generated — store a copy outside this container!"
else
  msg_ok "Backup key already exists"
fi

# ── Download HomeCA release ─────────────────────────────────────────
msg_info "Downloading HomeCA (${VERSION}) …"
TMPFILE=$(mktemp /tmp/homeca-release.XXXXXX.tar.gz)
gh_download_release "$VERSION" "homeca-linux-x64.tar.gz" "$TMPFILE"
tar -xzf "$TMPFILE" -C "$APP_DIR" --strip-components=0
rm -f "$TMPFILE"
chown -R root:root "$APP_DIR"
chmod 0755 "$APP_DIR"
msg_ok "HomeCA ${VERSION} deployed to ${APP_DIR}"

# ── systemd unit ────────────────────────────────────────────────────
msg_info "Installing systemd service …"
cat > "$SERVICE_FILE" << 'UNIT'
[Unit]
Description=HomeCA private certificate authority
After=network-online.target
Wants=network-online.target

[Service]
Type=exec
User=homeca
Group=homeca
WorkingDirectory=/opt/homeca
ExecStart=/usr/bin/dotnet /opt/homeca/HomeCA.Service.dll
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5080
Environment=Storage__RootPath=/var/lib/homeca
Environment=Storage__BackupPath=/var/backups/homeca
Environment=Storage__BackupKeyPath=/etc/homeca/backup.key
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/homeca /var/backups/homeca

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now homeca
msg_ok "Service enabled and started"

# ── Health check ────────────────────────────────────────────────────
msg_info "Waiting for health endpoint …"
HEALTHY=false
for i in $(seq 1 20); do
  if curl -sf http://127.0.0.1:5080/health >/dev/null 2>&1; then
    HEALTHY=true
    break
  fi
  sleep 2
done

if $HEALTHY; then
  msg_ok "HomeCA is healthy"
else
  msg_error "Health check failed — check logs: journalctl -u homeca -n 50"
fi

# ── Version marker (used by update script) ──────────────────────────
RESOLVED_TAG="$VERSION"
if [[ "$VERSION" == "latest" ]]; then
  RESOLVED_TAG=$(gh_resolve_latest_tag)
fi
echo "${RESOLVED_TAG}" > "${APP_DIR}/.homeca-version"

# ── Done ────────────────────────────────────────────────────────────
echo ""
msg_ok "HomeCA installation complete!"
echo -e " ${YW}Service:${CL}      systemctl status homeca"
echo -e " ${YW}Logs:${CL}         journalctl -u homeca -f"
echo -e " ${YW}Local URL:${CL}    http://127.0.0.1:5080"
echo -e " ${YW}Backup key:${CL}   ${CONFIG_DIR}/backup.key  ${RD}(save a copy!)${CL}"
echo ""
