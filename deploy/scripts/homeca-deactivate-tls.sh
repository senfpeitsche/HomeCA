#!/usr/bin/env bash
# HomeCA — TLS deactivation helper
# Restores the default HTTP listener while keeping TLS material for reactivation.
#
# Usage: bash /opt/homeca/homeca-deactivate-tls.sh

set -euo pipefail

SERVICE="homeca"
TLS_OVERRIDE="/etc/systemd/system/${SERVICE}.service.d/tls.conf"
TLS_OVERRIDE_BACKUP="/etc/homeca/tls.conf.disabled"
HTTP_HEALTH_URL="http://127.0.0.1:5080/health"

if [[ $EUID -ne 0 ]]; then
  echo "ERROR: This script must run as root." >&2
  exit 1
fi

if [[ ! -f "$TLS_OVERRIDE" ]]; then
  echo "HomeCA has no active TLS override. Verifying the HTTP listener …"
else
  echo "Saving TLS override to ${TLS_OVERRIDE_BACKUP} …"
  install -d -m 0750 /etc/homeca
  cp -p "$TLS_OVERRIDE" "$TLS_OVERRIDE_BACKUP"
  rm -f "$TLS_OVERRIDE"
fi

echo "Reloading systemd and restarting HomeCA on HTTP …"
systemctl daemon-reload
systemctl restart "$SERVICE"

echo "Waiting for HTTP health endpoint …"
for _ in $(seq 1 20); do
  if curl -sf "$HTTP_HEALTH_URL" >/dev/null 2>&1; then
    echo "HomeCA is running on HTTP: http://<hostname>:5080"
    echo "TLS configuration and certificate files were kept for later reactivation."
    exit 0
  fi
  sleep 2
done

echo "ERROR: HTTP health check did not respond within 40 seconds." >&2
echo "Restoring the previous TLS override …" >&2
if [[ -f "$TLS_OVERRIDE_BACKUP" ]]; then
  install -d -m 0755 "$(dirname "$TLS_OVERRIDE")"
  cp -p "$TLS_OVERRIDE_BACKUP" "$TLS_OVERRIDE"
  systemctl daemon-reload
  systemctl restart "$SERVICE" || true
fi
echo "Check logs: journalctl -u ${SERVICE} -n 50" >&2
exit 1
