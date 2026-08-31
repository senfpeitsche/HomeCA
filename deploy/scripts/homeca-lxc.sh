#!/usr/bin/env bash
# HomeCA LXC — Proxmox one-liner entry script
# Usage (paste into Proxmox shell):
#   GITHUB_TOKEN=ghp_… bash -c "$(curl -fsSL -H 'Authorization: token ghp_…' https://raw.githubusercontent.com/senfpeitsche/HomeCA/main/deploy/scripts/homeca-lxc.sh)"
#
# What it does:
#   1. Downloads the latest Debian 12 template (if missing)
#   2. Creates an unprivileged LXC with sensible defaults
#   3. Starts the container and runs the in-container install script
#
# Environment overrides (set before running):
#   GITHUB_TOKEN       — GitHub PAT for private repo access (required while repo is private)
#   HOMECA_CTID        — container ID          (default: next free ID)
#   HOMECA_HOSTNAME    — container hostname    (default: homeca)
#   HOMECA_DISK        — root disk in GB       (default: 8)
#   HOMECA_RAM         — memory in MiB         (default: 1024)
#   HOMECA_CORES       — CPU cores             (default: 1)
#   HOMECA_STORAGE     — Proxmox storage pool  (default: local-lvm)
#   HOMECA_BRIDGE      — network bridge        (default: vmbr0)
#   HOMECA_NET         — IP config             (default: dhcp)
#   HOMECA_VERSION     — release tag to install (default: latest)
#   HOMECA_REPO_URL    — raw base URL for scripts

set -euo pipefail

# ── Defaults ────────────────────────────────────────────────────────
REPO_URL="${HOMECA_REPO_URL:-https://raw.githubusercontent.com/senfpeitsche/HomeCA/main}"
HOSTNAME="${HOMECA_HOSTNAME:-homeca}"
DISK="${HOMECA_DISK:-8}"
RAM="${HOMECA_RAM:-1024}"
CORES="${HOMECA_CORES:-1}"
STORAGE="${HOMECA_STORAGE:-local-lvm}"
BRIDGE="${HOMECA_BRIDGE:-vmbr0}"
NET="${HOMECA_NET:-dhcp}"
VERSION="${HOMECA_VERSION:-latest}"

# ── Auth header for private repo ────────────────────────────────────
GH_AUTH=()
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  GH_AUTH=(-H "Authorization: token ${GITHUB_TOKEN}")
fi

# ── Colors / helpers ────────────────────────────────────────────────
BL='\033[36m' GN='\033[32m' RD='\033[31m' YW='\033[33m' CL='\033[0m'
msg_info()  { echo -e " ${BL}[INFO]${CL}  $1"; }
msg_ok()    { echo -e " ${GN}[OK]${CL}    $1"; }
msg_error() { echo -e " ${RD}[ERROR]${CL} $1"; }

# ── Pre-flight checks ──────────────────────────────────────────────
if ! command -v pveversion &>/dev/null; then
  msg_error "This script must run on a Proxmox VE host."
  exit 1
fi

PVE_VER=$(pveversion | grep -oP 'pve-manager/\K[0-9]+')
if (( PVE_VER < 8 )); then
  msg_error "Proxmox VE 8.x or newer is required."
  exit 1
fi

# ── Determine container ID ─────────────────────────────────────────
if [[ -n "${HOMECA_CTID:-}" ]]; then
  CTID="$HOMECA_CTID"
  if pct status "$CTID" &>/dev/null; then
    msg_error "Container $CTID already exists."
    exit 1
  fi
else
  CTID=$(pvesh get /cluster/nextid)
fi
msg_info "Using CT ID $CTID"

# ── Download Debian 12 template if needed ───────────────────────────
TEMPLATE_STORAGE="local"
TEMPLATE="debian-12-standard_12.7-1_amd64.tar.zst"
TEMPLATE_PATH="/var/lib/vz/template/cache/${TEMPLATE}"

if [[ ! -f "$TEMPLATE_PATH" ]]; then
  msg_info "Downloading Debian 12 template …"
  pveam update >/dev/null
  pveam download "$TEMPLATE_STORAGE" "$TEMPLATE" >/dev/null
  msg_ok "Template downloaded"
else
  msg_ok "Debian 12 template already cached"
fi

# ── IP configuration ────────────────────────────────────────────────
if [[ "$NET" == "dhcp" ]]; then
  NET_PARAM="name=eth0,bridge=${BRIDGE},ip=dhcp"
else
  # Expect CIDR notation, e.g. 10.0.0.50/24,gw=10.0.0.1
  NET_PARAM="name=eth0,bridge=${BRIDGE},ip=${NET}"
fi

# ── Create the LXC ──────────────────────────────────────────────────
msg_info "Creating LXC ${CTID} (${HOSTNAME}) …"
pct create "$CTID" "${TEMPLATE_STORAGE}:vztmpl/${TEMPLATE}" \
  --hostname "$HOSTNAME" \
  --cores "$CORES" \
  --memory "$RAM" \
  --swap 256 \
  --rootfs "${STORAGE}:${DISK}" \
  --net0 "$NET_PARAM" \
  --unprivileged 1 \
  --features nesting=0 \
  --onboot 1 \
  --start 0 \
  --ostype debian \
  >/dev/null
msg_ok "LXC ${CTID} created"

# ── Start and wait for network ───────────────────────────────────────
msg_info "Starting container …"
pct start "$CTID"

# Wait until the container has an IP (max 30 s)
for i in $(seq 1 30); do
  if pct exec "$CTID" -- ping -c1 -W1 deb.debian.org &>/dev/null; then
    break
  fi
  sleep 1
done
msg_ok "Container running"

# ── Run the in-container install script ──────────────────────────────
msg_info "Running HomeCA installer inside CT ${CTID} …"
pct exec "$CTID" -- bash -c "
  export HOMECA_VERSION='${VERSION}'
  export HOMECA_REPO_URL='${REPO_URL}'
  export GITHUB_TOKEN='${GITHUB_TOKEN:-}'
  bash <(curl -fsSL ${GH_AUTH[*]+"${GH_AUTH[*]}"} '${REPO_URL}/deploy/scripts/homeca-install.sh')
"
msg_ok "HomeCA installed"

# ── Summary ──────────────────────────────────────────────────────────
IP=$(pct exec "$CTID" -- hostname -I 2>/dev/null | awk '{print $1}')
echo ""
msg_ok "HomeCA LXC setup complete!"
echo -e " ${YW}Container ID:${CL}  ${CTID}"
echo -e " ${YW}Hostname:${CL}     ${HOSTNAME}"
echo -e " ${YW}Local URL:${CL}    http://127.0.0.1:5080  (inside CT)"
[[ -n "${IP:-}" ]] && echo -e " ${YW}CT IP:${CL}        ${IP}"
echo ""
echo -e " ${BL}Next steps:${CL}"
echo -e "   1. Attach to the container:  ${GN}pct enter ${CTID}${CL}"
echo -e "   2. Run the initial admin setup via the local endpoint"
echo -e "   3. Set up a reverse proxy for LAN access"
echo -e "   4. To update later, see docs/LXC-SETUP.md"
echo ""
