#!/usr/bin/env bash
# install.sh — publish the power-orchestrator as a self-contained linux-x64 binary and install
# it as a systemd service on the sentinel node (nuc-01). Idempotent: re-run to upgrade.
#
# Run from the dev box (needs the .NET SDK + ssh access to the node, and a populated secrets.env).
#
#   ./install.sh                      # deploy to nuc-01 (192.168.179.1)
#   ORCH_DEPLOY_HOST=192.168.179.1 ORCH_DEPLOY_USER=root ./install.sh
#
# The service starts in DRY-RUN (ORCH_ARMED unset/false): it observes + logs + emits telemetry
# but never powers anything off. Manual wake/sleep via the HTTP API still act. Arm later by
# setting ORCH_ARMED=true in the env file once the #191 blockers are cleared.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../../.." && pwd)"
PROJECT="$HERE/../src/PowerOrchestrator.Service/PowerOrchestrator.Service.csproj"
PUBLISH_DIR="$HERE/../publish"

HOST="${ORCH_DEPLOY_HOST:-192.168.179.1}"   # nuc-01
USER="${ORCH_DEPLOY_USER:-root}"
TARGET=/opt/power-orchestrator
SSH="ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new ${USER}@${HOST}"

echo "==> Publishing self-contained linux-x64 binary"
dotnet publish "$PROJECT" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=none -o "$PUBLISH_DIR"

# Build the systemd EnvironmentFile from the repo secrets.env (strip `export `, drop comments).
# Existing env on the node is preserved (we only overwrite if the operator re-runs).
ENV_TMP="$(mktemp)"
trap 'rm -f "$ENV_TMP"' EXIT
if [[ -f "$REPO_ROOT/secrets.env" ]]; then
    echo "==> Deriving EnvironmentFile from secrets.env"
    sed -E 's/^[[:space:]]*export[[:space:]]+//; /^[[:space:]]*#/d; /^[[:space:]]*$/d' \
        "$REPO_ROOT/secrets.env" > "$ENV_TMP"
else
    echo "!! secrets.env not found at repo root — writing a template; edit on the node before arming." >&2
    cat > "$ENV_TMP" <<'EOF'
# Fill these in (see secrets.env.example). Service stays in dry-run until ORCH_ARMED=true.
PROXMOX_BASE_URL=https://192.168.179.1:8006/api2/json
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
$SSH "mkdir -p ${TARGET}"
scp -q "$PUBLISH_DIR/power-orchestrator" "${USER}@${HOST}:${TARGET}/power-orchestrator"
# appsettings.json ships alongside the single-file binary (not embedded) — without it the
# service falls back to default logging levels.
scp -q "$PUBLISH_DIR/appsettings.json" "${USER}@${HOST}:${TARGET}/appsettings.json"
scp -q "$HERE/power-orchestrator.service" "${USER}@${HOST}:/etc/systemd/system/power-orchestrator.service"
scp -q "$ENV_TMP" "${USER}@${HOST}:${TARGET}/power-orchestrator.env"

$SSH "chmod 0700 ${TARGET}/power-orchestrator && chmod 0600 ${TARGET}/power-orchestrator.env \
   && systemctl daemon-reload \
   && systemctl enable --now power-orchestrator.service \
   && systemctl --no-pager --full status power-orchestrator.service | head -n 12"

echo "==> Done. Tail logs:  ${SSH} 'journalctl -u power-orchestrator -f'"
echo "    Status:        curl -s http://${HOST}:8080/status | jq"
