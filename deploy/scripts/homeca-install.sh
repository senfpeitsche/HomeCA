#!/usr/bin/env bash
# HomeCA — in-container install script
#
# Called automatically by homeca-lxc.sh, or run manually from a versioned GitHub Release asset.
#
# Environment overrides:
#   HOMECA_VERSION    — release tag (default: latest)

set -euo pipefail

VERSION="${HOMECA_VERSION:-latest}"
GH_REPO="senfpeitsche/HomeCA"

APP_DIR="/opt/homeca"
DATA_DIR="/var/lib/homeca"
BACKUP_DIR="/var/backups/homeca"
CONFIG_DIR="/etc/homeca"
SERVICE_FILE="/etc/systemd/system/homeca.service"

gh_resolve_latest_tag() {
  curl -fsSI "https://github.com/${GH_REPO}/releases/latest" 2>/dev/null \
    | grep -i '^location:' | grep -oP 'tag/\K[^\s\r]+'
}

resolve_release_tag() {
  if [[ "$VERSION" == "latest" ]]; then
    gh_resolve_latest_tag || msg_error "Could not resolve the latest HomeCA release tag."
  else
    printf '%s\n' "$VERSION"
  fi
}

download_and_verify_bundle() {
  local tag="$1" workdir="$2"
  local base="https://github.com/${GH_REPO}/releases/download/${tag}"
  local checksum expected
  curl -fsSL -o "$workdir/SHA256SUMS" "$base/SHA256SUMS"
  curl -fsSL -o "$workdir/homeca-release-bundle.tar.gz" "$base/homeca-release-bundle.tar.gz"
  checksum=$(awk '$2 == "homeca-release-bundle.tar.gz" { print $1 }' "$workdir/SHA256SUMS")
  [[ "$checksum" =~ ^[[:xdigit:]]{64}$ ]] || msg_error "Release checksum for the bundle is missing or invalid."
  printf '%s  %s\n' "$checksum" "homeca-release-bundle.tar.gz" | (cd "$workdir" && sha256sum --check --status -) \
    || msg_error "HomeCA release bundle checksum verification failed."
  tar -xzf "$workdir/homeca-release-bundle.tar.gz" -C "$workdir/bundle" --no-same-owner --no-same-permissions \
    || msg_error "HomeCA release bundle could not be extracted."
  [[ -f "$workdir/bundle/app/HomeCA.Service.dll" ]] \
    && [[ -f "$workdir/bundle/deploy/systemd/homeca.service" ]] \
    && [[ -f "$workdir/bundle/deploy/sudoers/homeca-tls" ]] \
    || msg_error "HomeCA release bundle is incomplete."
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
apt-get install -y -qq ca-certificates curl openssh-client apt-transport-https sudo
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

# ── CA private-key protection key ────────────────────────────────────
if [[ ! -f "${CONFIG_DIR}/ca.key" ]]; then
  msg_info "Generating CA private-key protection key …"
  umask 077
  head -c 32 /dev/urandom > "${CONFIG_DIR}/ca.key"
  chown homeca:homeca "${CONFIG_DIR}/ca.key"
  chmod 0600 "${CONFIG_DIR}/ca.key"
  msg_ok "CA key generated — store a copy outside this container!"
else
  chown homeca:homeca "${CONFIG_DIR}/ca.key"
  chmod 0600 "${CONFIG_DIR}/ca.key"
  msg_ok "CA private-key protection key already exists"
fi

# ── Download and verify the versioned release bundle ────────────────
RESOLVED_TAG=$(resolve_release_tag)
RELEASE_DIR=$(mktemp -d /tmp/homeca-release.XXXXXX)
trap 'rm -rf "$RELEASE_DIR"' EXIT
mkdir -p "$RELEASE_DIR/bundle"
msg_info "Downloading and verifying HomeCA ${RESOLVED_TAG} …"
download_and_verify_bundle "$RESOLVED_TAG" "$RELEASE_DIR"
cp -a "$RELEASE_DIR/bundle/app/." "$APP_DIR/"
chown -R root:root "$APP_DIR"
chmod 0755 "$APP_DIR"
msg_ok "HomeCA ${RESOLVED_TAG} deployed to ${APP_DIR}"

# ── TLS activation helper ──────────────────────────────────────────
msg_info "Installing release-bundled operational helpers …"
install -m 0755 "$RELEASE_DIR/bundle/deploy/scripts/homeca-activate-tls.sh" "${APP_DIR}/homeca-activate-tls.sh"
install -m 0755 "$RELEASE_DIR/bundle/deploy/scripts/homeca-deactivate-tls.sh" "${APP_DIR}/homeca-deactivate-tls.sh"
install -m 0755 "$RELEASE_DIR/bundle/deploy/scripts/homeca-update.sh" "${APP_DIR}/homeca-update.sh"
msg_ok "TLS helper installed"

# ── sudoers for web-triggered TLS activation ────────────────────────
msg_info "Installing sudoers drop-in for TLS activation …"
mkdir -p /etc/sudoers.d
install -m 0440 "$RELEASE_DIR/bundle/deploy/sudoers/homeca-tls" /etc/sudoers.d/homeca-tls
msg_ok "sudoers drop-in installed"

# ── systemd unit ────────────────────────────────────────────────────
msg_info "Installing systemd service …"
install -m 0644 "$RELEASE_DIR/bundle/deploy/systemd/homeca.service" "$SERVICE_FILE"

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
echo "${RESOLVED_TAG}" > "${APP_DIR}/.homeca-version"

# ── Done ────────────────────────────────────────────────────────────
echo ""
msg_ok "HomeCA installation complete!"
echo -e " ${YW}Service:${CL}      systemctl status homeca"
echo -e " ${YW}Logs:${CL}         journalctl -u homeca -f"
echo -e " ${YW}Local URL:${CL}    http://127.0.0.1:5080"
echo -e " ${YW}Backup key:${CL}   ${CONFIG_DIR}/backup.key  ${RD}(save a copy!)${CL}"
echo -e " ${YW}CA key:${CL}       ${CONFIG_DIR}/ca.key      ${RD}(save a copy!)${CL}"
echo ""
