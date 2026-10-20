#!/usr/bin/env bash
# HomeCA — TLS activation helper
# Called after the setup wizard writes /etc/homeca/tls.json
# Creates the systemd override and restarts the service on HTTPS.
#
# Usage: bash /opt/homeca/homeca-activate-tls.sh

set -euo pipefail

TLS_CONFIG="/etc/homeca/tls.json"
OVERRIDE_DIR="/etc/systemd/system/homeca.service.d"

if [[ ! -f "$TLS_CONFIG" ]]; then
  echo "ERROR: $TLS_CONFIG not found. Run the setup wizard first." >&2
  exit 1
fi

# Parse JSON with basic tools (no jq dependency)
HTTPS_URL=$(grep -oP '"httpsUrl"\s*:\s*"\K[^"]+' "$TLS_CONFIG")
PFX_PATH=$(grep -oP '"pfxPath"\s*:\s*"\K[^"]+' "$TLS_CONFIG")
PUBLIC_URL=$(grep -oP '"publicUrl"\s*:\s*"\K[^"]+' "$TLS_CONFIG")
HOSTNAME_VALUE=$(grep -oP '"hostname"\s*:\s*"\K[^"]+' "$TLS_CONFIG" || true)
# Plain HTTP stays enabled for CRL distribution points, trust bootstrap and ACME clients.
HTTP_URL=$(grep -oP '"httpUrl"\s*:\s*"\K[^"]+' "$TLS_CONFIG" || echo "http://0.0.0.0:5080")
CRL_BASE_URL=$(grep -oP '"crlBaseUrl"\s*:\s*"\K[^"]+' "$TLS_CONFIG" || true)
if [[ -z "$CRL_BASE_URL" && -n "$HOSTNAME_VALUE" ]]; then
  CRL_BASE_URL="http://${HOSTNAME_VALUE}:5080"
fi

if [[ -z "$HTTPS_URL" || -z "$PFX_PATH" ]]; then
  echo "ERROR: Invalid TLS configuration in $TLS_CONFIG" >&2
  exit 1
fi

if [[ ! -f "$PFX_PATH" ]]; then
  echo "ERROR: Certificate file not found: $PFX_PATH" >&2
  exit 1
fi

echo "Creating systemd override for HTTPS …"
mkdir -p "$OVERRIDE_DIR"
cat > "${OVERRIDE_DIR}/tls.conf" << EOF
[Service]
Environment=ASPNETCORE_URLS=${HTTP_URL};${HTTPS_URL}
Environment=ASPNETCORE_Kestrel__Certificates__Default__Path=${PFX_PATH}
Environment=Storage__PublicUrl=${PUBLIC_URL}
EOF
if [[ -n "$CRL_BASE_URL" ]]; then
  echo "Environment=Storage__CrlBaseUrl=${CRL_BASE_URL}" >> "${OVERRIDE_DIR}/tls.conf"
fi

echo "Reloading systemd and restarting HomeCA …"
systemctl daemon-reload
systemctl restart homeca

# Wait for health check on HTTPS
echo "Waiting for HTTPS health endpoint …"
for i in $(seq 1 20); do
  if curl -sfk "${HTTPS_URL}/health" >/dev/null 2>&1; then
    echo "HomeCA is running on HTTPS: ${PUBLIC_URL}"
    exit 0
  fi
  sleep 2
done

echo "WARNING: Health check on HTTPS did not respond within 40s."
echo "Check logs: journalctl -u homeca -n 50"
exit 1
