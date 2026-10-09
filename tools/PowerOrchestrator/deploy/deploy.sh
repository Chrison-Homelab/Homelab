#!/usr/bin/env bash
# deploy.sh — the "deploy sugar": copy the already-published power-orchestrator binary onto the
# sentinel node (nuc-01) and (re)install it as a systemd service. Idempotent: re-run to upgrade.
#
# Fallout owns the build/test/publish (native dotnet). This script consumes Fallout's publish/
# output — it does NOT build. Drive it through the Fallout target so publish always runs first:
#
#   ./build.sh DeployPowerOrchestrator                          # publish → copy → systemd (recommended)
#   ORCH_DEPLOY_HOST=nuc-01.homelab.chrison.internal ./build.sh DeployPowerOrchestrator
#
# Running this script directly is supported too, but only after a publish exists:
#   ./build.sh PublishPowerOrchestrator && tools/PowerOrchestrator/deploy/deploy.sh
#
# The service starts in DRY-RUN (ORCH_ARMED unset/false): it observes + logs + emits telemetry
# but never powers anything off. Manual wake/sleep via the HTTP API still act. Arm later by
# setting ORCH_ARMED=true in the env file once the #191 blockers are cleared.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../../.." && pwd)"
PUBLISH_DIR="$HERE/../publish"

HOST="${ORCH_DEPLOY_HOST:-nuc-01.homelab.chrison.internal}"   # the sentinel node, by its UniFi name
USER="${ORCH_DEPLOY_USER:-root}"
TARGET=/opt/power-orchestrator
SSH="ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new ${USER}@${HOST}"

# Require Fallout's publish output — this script copies, it never builds.
if [[ ! -x "$PUBLISH_DIR/power-orchestrator" ]]; then
    echo "!! No published binary at $PUBLISH_DIR — run the publish first:" >&2
    echo "     ./build.sh PublishPowerOrchestrator   (or just ./build.sh DeployPowerOrchestrator)" >&2
    exit 1
fi

# Build the systemd EnvironmentFile from the repo secrets.env. Scope it to ONLY the keys this
# service needs (minimal creds — don't ship Cloudflare/GitHub/Synology tokens to the node):
# Proxmox (idle/guest-shutdown), UniFi (presence), ORCH_* tunables, OTEL endpoint, bind URL.
ENV_TMP="$(mktemp)"
trap 'rm -f "$ENV_TMP"' EXIT
ORCH_KEY_RE='^[[:space:]]*(export[[:space:]]+)?(PROXMOX_|UNIFI_|ORCH_|OTEL_EXPORTER|ASPNETCORE_URLS)'
if [[ -f "$REPO_ROOT/secrets.env" ]]; then
    echo "==> Deriving EnvironmentFile from secrets.env (orchestrator keys only)"
    grep -E "$ORCH_KEY_RE" "$REPO_ROOT/secrets.env" | sed -E 's/^[[:space:]]*export[[:space:]]+//' > "$ENV_TMP"
else
    echo "!! secrets.env not found at repo root — writing a template; edit on the node before arming." >&2
    cat > "$ENV_TMP" <<'EOF'
# Fill these in (see secrets.env.template / run scripts/secrets-sync.sh). Service stays in dry-run until ORCH_ARMED=true.
PROXMOX_BASE_URL=https://hpe-01.homelab.chrison.internal:8006/api2/json
PROXMOX_TOKEN_ID=
PROXMOX_TOKEN_SECRET=
PROXMOX_VERIFY_TLS=false
UNIFI_BASE_URL=https://192.168.178.1/proxy/network/integration
UNIFI_API_KEY=
UNIFI_VERIFY_TLS=false
ORCH_PRESENCE_MACS=
OTEL_EXPORTER_OTLP_ENDPOINT=
EOF
fi
# Ensure the service binds somewhere predictable.
grep -q '^ASPNETCORE_URLS=' "$ENV_TMP" || echo 'ASPNETCORE_URLS=http://0.0.0.0:8080' >> "$ENV_TMP"

echo "==> Installing to ${USER}@${HOST}:${TARGET}"
# Stop first: you can't overwrite a running executable on Linux (ETXTBSY), so an upgrade scp
# over a live binary fails. Stopping is safe — the service is restarted at the end.
$SSH "systemctl stop power-orchestrator.service 2>/dev/null || true; mkdir -p ${TARGET}"
# Sync the whole publish output, not just the binary: appsettings.json (logging config) and the
# Blazor static-asset manifest (power-orchestrator.staticwebassets.endpoints.json) + wwwroot ship
# alongside the single-file binary and are needed at runtime for the web dashboard.
scp -rq "$PUBLISH_DIR/." "${USER}@${HOST}:${TARGET}/"
scp -q "$HERE/power-orchestrator.service" "${USER}@${HOST}:/etc/systemd/system/power-orchestrator.service"
scp -q "$ENV_TMP" "${USER}@${HOST}:${TARGET}/power-orchestrator.env"

# restart (not just enable --now) so re-running the installer actually rolls the new binary.
$SSH "chmod 0700 ${TARGET}/power-orchestrator && chmod 0600 ${TARGET}/power-orchestrator.env \
   && systemctl daemon-reload \
   && systemctl enable power-orchestrator.service \
   && systemctl restart power-orchestrator.service \
   && systemctl --no-pager --full status power-orchestrator.service | head -n 12"

echo "==> Done. Tail logs:  ${SSH} 'journalctl -u power-orchestrator -f'"
echo "    Status:        curl -s http://${HOST}:8080/status | jq"
