#!/usr/bin/env bash
# HomeCA — update script
#
# Run inside the container (the installed script is release-bundled):
#   /opt/homeca/homeca-update.sh
#
# Environment overrides:
#   HOMECA_VERSION     — target release tag (default: latest)
#   HOMECA_SKIP_BACKUP — set to 1 to skip the pre-update backup (not recommended)

set -euo pipefail

VERSION="${HOMECA_VERSION:-latest}"
GH_REPO="senfpeitsche/HomeCA"

APP_DIR="/opt/homeca"
DATA_DIR="/var/lib/homeca"
BACKUP_DIR="/var/backups/homeca"
SERVICE="homeca"

gh_resolve_latest_tag() {
  curl -fsSI "https://github.com/${GH_REPO}/releases/latest" 2>/dev/null \
    | grep -i '^location:' | grep -oP 'tag/\K[^\s\r]+'
}

download_and_verify_bundle() {
  local tag="$1" workdir="$2"
  local base="https://github.com/${GH_REPO}/releases/download/${tag}"
  local checksum
  curl -fsSL -o "$workdir/SHA256SUMS" "$base/SHA256SUMS"
  curl -fsSL -o "$workdir/homeca-release-bundle.tar.gz" "$base/homeca-release-bundle.tar.gz"
  checksum=$(awk '$2 == "homeca-release-bundle.tar.gz" { print $1 }' "$workdir/SHA256SUMS")
  [[ "$checksum" =~ ^[[:xdigit:]]{64}$ ]] || msg_error "Release checksum for the bundle is missing or invalid."
  printf '%s  %s\n' "$checksum" "homeca-release-bundle.tar.gz" | (cd "$workdir" && sha256sum --check --status -) \
    || msg_error "HomeCA release bundle checksum verification failed. The current installation was not changed."
  mkdir -p "$workdir/bundle"
  tar -xzf "$workdir/homeca-release-bundle.tar.gz" -C "$workdir/bundle" --no-same-owner --no-same-permissions \
    || msg_error "HomeCA release bundle could not be extracted. The current installation was not changed."
  [[ -f "$workdir/bundle/app/HomeCA.Service.dll" ]] \
    && [[ -f "$workdir/bundle/deploy/systemd/homeca.service" ]] \
    && [[ -f "$workdir/bundle/deploy/sudoers/homeca-tls" ]] \
    || msg_error "HomeCA release bundle is incomplete. The current installation was not changed."
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

if [[ ! -f "${APP_DIR}/HomeCA.Service.dll" ]]; then
  msg_error "HomeCA is not installed. Run homeca-install.sh first."
fi

# Older installations did not install sudo, although it is required by the
# passwordless sudoers rule used for TLS activation from the web UI.
if ! command -v sudo >/dev/null 2>&1; then
  msg_info "Installing missing TLS activation dependency (sudo) …"
  apt-get update -qq
  apt-get install -y -qq sudo
  msg_ok "sudo installed"
fi

CURRENT="unknown"
if [[ -f "${APP_DIR}/.homeca-version" ]]; then
  CURRENT=$(cat "${APP_DIR}/.homeca-version")
fi
msg_info "Current version: ${CURRENT}"
msg_info "Target version:  ${VERSION}"

# ── Resolve latest tag for comparison ───────────────────────────────
if [[ "$VERSION" == "latest" ]]; then
  RESOLVED_TAG=$(gh_resolve_latest_tag) || msg_error "Could not resolve the latest HomeCA release tag."
else
  RESOLVED_TAG="$VERSION"
fi

if [[ "$RESOLVED_TAG" == "$CURRENT" && "$RESOLVED_TAG" != "latest" ]]; then
  msg_ok "Already on version ${CURRENT} — nothing to do."
  exit 0
fi

# ── Download and verify before changing the running installation ────
RELEASE_DIR=$(mktemp -d /tmp/homeca-release.XXXXXX)
trap 'rm -rf "$RELEASE_DIR"' EXIT
msg_info "Downloading and verifying HomeCA ${RESOLVED_TAG} …"
download_and_verify_bundle "$RESOLVED_TAG" "$RELEASE_DIR"
msg_ok "Release bundle checksum verified"

# ── System update ───────────────────────────────────────────────────
msg_info "Updating system packages …"
apt-get update -qq
apt-get full-upgrade -y -qq
msg_ok "System updated"

# ── Pre-update backup ──────────────────────────────────────────────
if [[ "${HOMECA_SKIP_BACKUP:-0}" != "1" ]]; then
  msg_info "Creating pre-update backup …"
  BACKUP_TS=$(date +%Y%m%d-%H%M%S)
  BACKUP_FILE="${BACKUP_DIR}/pre-update-${BACKUP_TS}.tar.gz"
  tar -czf "$BACKUP_FILE" \
    -C / \
    "var/lib/homeca" \
    "etc/homeca" \
    2>/dev/null || true
  chown homeca:homeca "$BACKUP_FILE"
  chmod 0600 "$BACKUP_FILE"
  msg_ok "Backup saved to ${BACKUP_FILE}"
else
  msg_info "Skipping backup (HOMECA_SKIP_BACKUP=1)"
fi

# ── Stop service ────────────────────────────────────────────────────
msg_info "Stopping HomeCA …"
systemctl stop "$SERVICE"
msg_ok "Service stopped"

# ── Keep previous release for rollback ──────────────────────────────
ROLLBACK_DIR="${APP_DIR}.rollback"
if [[ -d "$ROLLBACK_DIR" ]]; then
  rm -rf "$ROLLBACK_DIR"
fi
cp -a "$APP_DIR" "$ROLLBACK_DIR"
msg_ok "Previous release saved to ${ROLLBACK_DIR}"

# ── Deploy verified release bundle ──────────────────────────────────
msg_info "Deploying verified HomeCA ${RESOLVED_TAG} …"
rm -rf "${APP_DIR:?}"/*
cp -a "$RELEASE_DIR/bundle/app/." "$APP_DIR/"
chown -R root:root "$APP_DIR"
chmod 0755 "$APP_DIR"
msg_ok "HomeCA ${RESOLVED_TAG} deployed"

# ── Update systemd unit (in case it changed) ────────────────────────
msg_info "Refreshing release-bundled systemd unit …"
install -m 0644 "$RELEASE_DIR/bundle/deploy/systemd/homeca.service" /etc/systemd/system/homeca.service
systemctl daemon-reload
msg_ok "systemd unit updated"

# ── Update sudoers drop-in for web-triggered TLS activation ─────────
msg_info "Refreshing release-bundled sudoers drop-in …"
mkdir -p /etc/sudoers.d
install -m 0440 "$RELEASE_DIR/bundle/deploy/sudoers/homeca-tls" /etc/sudoers.d/homeca-tls
msg_ok "sudoers drop-in updated"

# ── Refresh TLS helper scripts ──────────────────────────────────────
msg_info "Refreshing release-bundled operational helpers …"
for helper in homeca-activate-tls.sh homeca-deactivate-tls.sh homeca-update.sh; do
  install -m 0755 "$RELEASE_DIR/bundle/deploy/scripts/${helper}" "${APP_DIR}/${helper}"
done
msg_ok "Operational helpers refreshed"

# ── Start service ───────────────────────────────────────────────────
msg_info "Starting HomeCA …"
systemctl start "$SERVICE"

# ── Health check ────────────────────────────────────────────────────
# TLS activation keeps its configuration outside the release directory, so it
# survives an update. When present, probe the same HTTPS listener that systemd
# restores through its TLS override. The certificate may be issued by HomeCA's
# private CA, therefore curl must not require it to be trusted locally.
HEALTH_URL="http://127.0.0.1:5080/health"
TLS_CONFIG="/etc/homeca/tls.json"
if [[ -f "$TLS_CONFIG" ]]; then
  HTTPS_URL=$(grep -oP '"httpsUrl"\s*:\s*"\K[^"]+' "$TLS_CONFIG" || true)
  if [[ "$HTTPS_URL" =~ ^https://[^/]+/?$ ]]; then
    HEALTH_URL="${HTTPS_URL%/}/health"
  fi
fi

msg_info "Waiting for health endpoint (${HEALTH_URL}) …"
HEALTHY=false
for i in $(seq 1 20); do
  if curl -sfk "$HEALTH_URL" >/dev/null 2>&1; then
    HEALTHY=true
    break
  fi
  sleep 2
done

if $HEALTHY; then
  echo "${RESOLVED_TAG}" > "${APP_DIR}/.homeca-version"
  rm -rf "$ROLLBACK_DIR"
  msg_ok "HomeCA is healthy"
else
  msg_info "Health check failed — rolling back …"
  systemctl stop "$SERVICE"
  rm -rf "$APP_DIR"
  mv "$ROLLBACK_DIR" "$APP_DIR"
  systemctl start "$SERVICE"
  msg_error "Update failed health check. Rolled back to ${CURRENT}. Check logs: journalctl -u homeca -n 50"
fi

# ── Done ────────────────────────────────────────────────────────────
echo ""
msg_ok "HomeCA updated: ${CURRENT} → ${RESOLVED_TAG}"
echo -e " ${YW}Service:${CL}   systemctl status homeca"
echo -e " ${YW}Logs:${CL}      journalctl -u homeca -f"
echo ""
