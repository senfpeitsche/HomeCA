#!/usr/bin/env bash
# HomeCA LXC — Proxmox VE entry script (published as release asset homeca-lxc.sh)
#
# Creates a Debian 13 container through the community-scripts/core engine, which
# asks for storage, template, network and advanced settings, and then installs
# HomeCA inside with install/homeca-install.sh from the same release.
#
# Environment overrides (set before running):
#   HOMECA_VERSION     — release tag to install (default: the release this script belongs to)
#   HOMECA_CTID        — container ID          → var_ctid
#   HOMECA_HOSTNAME    — container hostname    → var_hostname
#   HOMECA_DISK        — root disk in GB       → var_disk
#   HOMECA_RAM         — memory in MiB         → var_ram
#   HOMECA_CORES       — CPU cores             → var_cpu
#   HOMECA_STORAGE     — container storage     → var_container_storage
#   HOMECA_BRIDGE      — network bridge        → var_brg
#   HOMECA_NET         — dhcp or CIDR[,gw=IP]  → var_net / var_gateway
#   Every other core var_* setting (var_vlan, var_ssh, var_template_storage, …) works too.
#   COMMUNITY_SCRIPTS_CORE_URL — core engine base URL (default: pinned commit below)

GH_REPO="senfpeitsche/HomeCA"
HOMECA_RELEASE="latest" # replaced with the release tag by the release workflow
# Pinned so a HomeCA release always runs against the engine it was tested with
CORE_COMMIT="9a4ceea0d8b83d213f058cd44617f3fe464ec7a9"

HOMECA_VERSION="${HOMECA_VERSION:-$HOMECA_RELEASE}"
if [[ "$HOMECA_VERSION" == "latest" ]]; then
  HOMECA_VERSION=$(curl -fsSI "https://github.com/${GH_REPO}/releases/latest" 2>/dev/null \
    | grep -i '^location:' | grep -oP 'tag/\K[^\s\r]+' || true)
  if [[ -z "$HOMECA_VERSION" ]]; then
    echo "Could not resolve the latest HomeCA release tag." >&2
    exit 1
  fi
fi
# lxc-attach keeps the environment, so the install script sees the same tag
export HOMECA_VERSION

# install/homeca-install.sh is fetched from this repository at the same tag
export COMMUNITY_SCRIPTS_URL="${COMMUNITY_SCRIPTS_URL:-https://raw.githubusercontent.com/${GH_REPO}/${HOMECA_VERSION}/deploy/proxmox}"
export COMMUNITY_SCRIPTS_CORE_URL="${COMMUNITY_SCRIPTS_CORE_URL:-https://raw.githubusercontent.com/community-scripts/core/${CORE_COMMIT}}"

# Keep the HOMECA_* overrides of the previous standalone script working
[[ -n "${HOMECA_CTID:-}" ]] && var_ctid="${var_ctid:-$HOMECA_CTID}"
[[ -n "${HOMECA_HOSTNAME:-}" ]] && var_hostname="${var_hostname:-$HOMECA_HOSTNAME}"
[[ -n "${HOMECA_DISK:-}" ]] && var_disk="${var_disk:-$HOMECA_DISK}"
[[ -n "${HOMECA_RAM:-}" ]] && var_ram="${var_ram:-$HOMECA_RAM}"
[[ -n "${HOMECA_CORES:-}" ]] && var_cpu="${var_cpu:-$HOMECA_CORES}"
[[ -n "${HOMECA_STORAGE:-}" ]] && var_container_storage="${var_container_storage:-$HOMECA_STORAGE}"
[[ -n "${HOMECA_BRIDGE:-}" ]] && var_brg="${var_brg:-$HOMECA_BRIDGE}"
if [[ -n "${HOMECA_NET:-}" ]]; then
  var_net="${var_net:-${HOMECA_NET%%,*}}"
  [[ "$HOMECA_NET" == *",gw="* ]] && var_gateway="${var_gateway:-${HOMECA_NET##*,gw=}}"
fi

# A local core checkout wins (COMMUNITY_SCRIPTS_CORE_DIR), so a fork can be tested without editing this file
# shellcheck source=/dev/null
if [[ -n "${COMMUNITY_SCRIPTS_CORE_DIR:-}" ]]; then
  source "${COMMUNITY_SCRIPTS_CORE_DIR}/core/build.func"
else
  source <(curl -fsSL "${COMMUNITY_SCRIPTS_CORE_URL}/core/build.func")
fi

APP="HomeCA"
var_tags="${var_tags:-security;pki;certificates}"
var_cpu="${var_cpu:-1}"
var_ram="${var_ram:-1024}"
var_disk="${var_disk:-8}"
var_os="${var_os:-debian}"
var_version="${var_version:-13}"
var_unprivileged="${var_unprivileged:-1}"
var_arm64="${var_arm64:-no}" # releases publish a linux-x64 build only

header_info "$APP"
variables
color
catch_errors

function update_script() {
  header_info
  check_container_storage
  check_container_resources

  if [[ ! -x /opt/homeca/homeca-update.sh ]]; then
    msg_error "No ${APP} Installation Found!"
    exit
  fi

  # Backs up, verifies the release bundle and rolls back on a failed health check
  /opt/homeca/homeca-update.sh
  exit
}

start
build_container
description

msg_ok "Completed Successfully!\n"
echo -e "${CREATING}${GN}${APP} ${HOMECA_VERSION} setup has been successfully initialized!${CL}"
echo -e "${INFO}${YW}Access it using the following URL:${CL}"
echo -e "${TAB}${GATEWAY}${BGN}http://${IP}:5080${CL}"
echo -e "${INFO}${YW}Copy /etc/homeca/ca.key and /etc/homeca/backup.key out of the container and keep them safe.${CL}"
