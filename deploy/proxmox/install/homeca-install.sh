#!/usr/bin/env bash
# HomeCA — community-scripts/core container install step
#
# Fetched and run inside the new container by core's build_container. Prepares the
# container with core's helpers and then runs the release's homeca-install.sh, which
# installs HomeCA with its service user, sudoers drop-in and verified release bundle.
#
# Expects HOMECA_VERSION (release tag), exported by ct/homeca.sh.

# shellcheck source=/dev/null
source /dev/stdin <<<"$FUNCTIONS_FILE_PATH"
color
verb_ip6
catch_errors
setting_up_container
network_check
update_os

GH_REPO="senfpeitsche/HomeCA"

if [[ -z "${HOMECA_VERSION:-}" ]]; then
  msg_error "HOMECA_VERSION is not set. Start the setup with homeca-lxc.sh."
  exit 1
fi

msg_info "Downloading HomeCA installer ${HOMECA_VERSION}"
INSTALLER_DIR=$(mktemp -d /tmp/homeca-installer.XXXXXX)
RELEASE_URL="https://github.com/${GH_REPO}/releases/download/${HOMECA_VERSION}"
curl -fsSL -o "${INSTALLER_DIR}/SHA256SUMS" "${RELEASE_URL}/SHA256SUMS"
curl -fsSL -o "${INSTALLER_DIR}/homeca-install.sh" "${RELEASE_URL}/homeca-install.sh"
if ! (cd "$INSTALLER_DIR" && grep ' homeca-install.sh$' SHA256SUMS | sha256sum --check --status -); then
  msg_error "Checksum verification of homeca-install.sh failed."
  exit 1
fi
msg_ok "Downloaded HomeCA installer ${HOMECA_VERSION}"

msg_info "Installing HomeCA ${HOMECA_VERSION}"
$STD env HOMECA_VERSION="$HOMECA_VERSION" bash "${INSTALLER_DIR}/homeca-install.sh"
rm -rf "$INSTALLER_DIR"
msg_ok "Installed HomeCA ${HOMECA_VERSION}"

motd_ssh
customize
cleanup_lxc
