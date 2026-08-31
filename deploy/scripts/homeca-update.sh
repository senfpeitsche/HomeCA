#!/usr/bin/env bash
# HomeCA — update script
#
# Run inside the container (private repo):
#   GITHUB_TOKEN=ghp_… bash <(curl -fsSL \
#     -H 'Authorization: token ghp_…' -H 'Accept: application/vnd.github.raw' \
#     'https://api.github.com/repos/senfpeitsche/HomeCA/contents/deploy/scripts/homeca-update.sh?ref=main')
#
# From the Proxmox host (replace 100 with your CT ID):
#   curl -fsSL -H 'Authorization: token ghp_…' -H 'Accept: application/vnd.github.raw' \
#     'https://api.github.com/repos/senfpeitsche/HomeCA/contents/deploy/scripts/homeca-update.sh?ref=main' \
#     -o /tmp/homeca-update.sh
#   pct push 100 /tmp/homeca-update.sh /tmp/homeca-update.sh
#   pct exec 100 -- bash -c "export GITHUB_TOKEN='ghp_…'; bash /tmp/homeca-update.sh"
#
# Environment overrides:
#   GITHUB_TOKEN       — GitHub PAT for private repo access (required while repo is private)
#   HOMECA_VERSION     — target release tag (default: latest)
#   HOMECA_SKIP_BACKUP — set to 1 to skip the pre-update backup (not recommended)

set -euo pipefail

VERSION="${HOMECA_VERSION:-latest}"
GH_REPO="senfpeitsche/HomeCA"

APP_DIR="/opt/homeca"
DATA_DIR="/var/lib/homeca"
BACKUP_DIR="/var/backups/homeca"
SERVICE="homeca"

# ── Auth + download helpers for private repo ────────────────────────
GH_AUTH_HEADER=()
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  GH_AUTH_HEADER=(-H "Authorization: token ${GITHUB_TOKEN}")
fi

gh_download_release() {
  local version="$1" asset="$2" dest="$3"
  if [[ -n "${GITHUB_TOKEN:-}" ]]; then
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
    curl -fsSL "${GH_AUTH_HEADER[@]}" -H "Accept: application/octet-stream" \
      -o "$dest" -L "$asset_url"
  else
    local base="https://github.com/${GH_REPO}/releases"
    if [[ "$version" == "latest" ]]; then
      curl -fsSL -o "$dest" "${base}/latest/download/${asset}"
    else
      curl -fsSL -o "$dest" "${base}/download/${version}/${asset}"
    fi
  fi
}

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

if [[ ! -f "${APP_DIR}/HomeCA.Service.dll" ]]; then
  msg_error "HomeCA is not installed. Run homeca-install.sh first."
fi

CURRENT="unknown"
if [[ -f "${APP_DIR}/.homeca-version" ]]; then
  CURRENT=$(cat "${APP_DIR}/.homeca-version")
fi
msg_info "Current version: ${CURRENT}"
msg_info "Target version:  ${VERSION}"

# ── Resolve latest tag for comparison ───────────────────────────────
if [[ "$VERSION" == "latest" ]]; then
  RESOLVED_TAG=$(gh_resolve_latest_tag)
else
  RESOLVED_TAG="$VERSION"
fi

if [[ "$RESOLVED_TAG" == "$CURRENT" && "$RESOLVED_TAG" != "latest" ]]; then
  msg_ok "Already on version ${CURRENT} — nothing to do."
  exit 0
fi

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

# ── Download and deploy new release ─────────────────────────────────
msg_info "Downloading HomeCA (${RESOLVED_TAG}) …"
TMPFILE=$(mktemp /tmp/homeca-release.XXXXXX.tar.gz)
if ! gh_download_release "$RESOLVED_TAG" "homeca-linux-x64.tar.gz" "$TMPFILE"; then
  msg_info "Download failed — rolling back …"
  rm -rf "$APP_DIR"
  mv "$ROLLBACK_DIR" "$APP_DIR"
  systemctl start "$SERVICE"
  msg_error "Download failed. Rolled back to ${CURRENT}."
fi

rm -rf "${APP_DIR:?}"/*
tar -xzf "$TMPFILE" -C "$APP_DIR" --strip-components=0
rm -f "$TMPFILE"
chown -R root:root "$APP_DIR"
chmod 0755 "$APP_DIR"
msg_ok "HomeCA ${RESOLVED_TAG} deployed"

# ── Update systemd unit (in case it changed) ────────────────────────
msg_info "Refreshing systemd unit …"
if gh_raw "deploy/systemd/homeca.service" /tmp/homeca.service.new 2>/dev/null; then
  if ! diff -q /tmp/homeca.service.new /etc/systemd/system/homeca.service &>/dev/null; then
    cp /tmp/homeca.service.new /etc/systemd/system/homeca.service
    systemctl daemon-reload
    msg_ok "systemd unit updated"
  else
    msg_ok "systemd unit unchanged"
  fi
  rm -f /tmp/homeca.service.new
else
  msg_ok "Could not fetch remote unit — keeping current"
fi

# ── Start service ───────────────────────────────────────────────────
msg_info "Starting HomeCA …"
systemctl start "$SERVICE"

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
