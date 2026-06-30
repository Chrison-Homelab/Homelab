#!/usr/bin/env bash
# install.sh — deploy the stacks/monitoring compose onto the observability Docker
# LXC (CT 4000 on hpe-01, #222). Idempotent: re-run to update configs + roll the
# stack. No manual layer-on.
#
# Reaches the CT THROUGH its Proxmox node (ssh <node> → pct push/exec), so the CT
# itself needs no SSH key — same trust path converge uses. Run from the dev box
# (needs ssh access to the node, as converge does).
#
#   ./install.sh
#   MON_NODE_HOST=hpe-01 MON_CTID=4000 ./install.sh
#
# Brings up the OTel pipeline + dashboards (prometheus grafana otel-collector
# tempo loki) — the #222 acceptance set. The servarr/snmp exporters are left out
# here (they need .env.local arr keys + SNMP config); add them later.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STACK_DIR="$(cd "$HERE/.." && pwd)"

NODE_HOST="${MON_NODE_HOST:-hpe-01}"          # Proxmox node the CT lives on (ssh target)
NODE_USER="${MON_NODE_USER:-root}"
CTID="${MON_CTID:-4000}"
TARGET="${MON_TARGET:-/opt/monitoring}"
# snmp_exporter is intentionally NOT in the default set yet: config/snmp.yml is a
# stub (no generated Synology module), so the exporter would crash-loop on
# v0.30.1. Add it back here once a real module is generated (see README / #222).
SERVICES="${MON_SERVICES:-prometheus grafana otel-collector tempo loki}"
SSH="ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new ${NODE_USER}@${NODE_HOST}"

echo "==> Packing ${STACK_DIR} (compose + sample configs + grafana provisioning + .env)"
TAR_TMP="$(mktemp -t monitoring.XXXX.tgz)"
trap 'rm -f "$TAR_TMP"' EXIT
# Ship the compose, sample configs, grafana provisioning/dashboards and the .env
# defaults. Exclude runtime data, local secrets, and any already-materialised
# real configs (those are regenerated from sample.* on the CT below).
# COPYFILE_DISABLE + --exclude '._*': macOS bsdtar otherwise embeds AppleDouble
# (._foo) sidecar files; grafana then tries to parse ._dashboards.yml as a
# provisioning config and crashes ("control characters are not allowed").
# Ship .env.local too when it exists — it holds the REAL secrets (Grafana admin
# password, servarr keys) that override the safe .env defaults. Gitignored, so
# it only travels host→CT, never into git. Without it Grafana runs on the
# changeme default — never acceptable on a live host.
ENV_LOCAL=()
[ -f "$STACK_DIR/.env.local" ] && ENV_LOCAL=(.env.local)
COPYFILE_DISABLE=1 tar czf "$TAR_TMP" -C "$STACK_DIR" \
    --exclude='./data' --exclude='./.git' --exclude='./deploy' \
    --exclude='._*' --exclude='.DS_Store' \
    compose.yml .env "${ENV_LOCAL[@]}" config grafana

echo "==> Pushing into CT ${CTID} on ${NODE_HOST}:${TARGET}"
$SSH "pct exec ${CTID} -- mkdir -p ${TARGET}"
# Stage on the node, then pct push into the CT (pct push needs a node-local file).
$SSH "cat > /tmp/monitoring.tgz" < "$TAR_TMP"
$SSH "pct push ${CTID} /tmp/monitoring.tgz /tmp/monitoring.tgz && rm -f /tmp/monitoring.tgz"
$SSH "pct exec ${CTID} -- tar xzf /tmp/monitoring.tgz -C ${TARGET} && pct exec ${CTID} -- rm -f /tmp/monitoring.tgz"

echo "==> Materialising sample.* configs (repo is authoritative — overwrites)"
# sample.<name>.yml → <name>.yml. Overwrite so the repo is the source of truth:
# config changes (e.g. the snmp/prometheus scrape jobs) actually redeploy rather
# than being skipped because a stale real file already exists. Secrets live in
# .env/.env.local, never in these configs, so overwriting is safe.
$SSH "pct exec ${CTID} -- bash -c 'cd ${TARGET}/config && for s in sample.*.yml; do cp -f \"\$s\" \"\${s#sample.}\"; done && ls *.yml'"

# Bind-mounted data dirs are created root-owned, but grafana (uid 472) and
# loki/tempo (uid 10001) run non-root and must own their volumes — else they
# crash-loop on "permission denied". Pre-create + chown (idempotent).
echo "==> Preparing data dirs (grafana uid 472, loki/tempo uid 10001)"
$SSH "pct exec ${CTID} -- bash -c 'cd ${TARGET} && mkdir -p data/grafana data/loki data/tempo \
    && chown -R 472:472 data/grafana && chown -R 10001:10001 data/loki data/tempo'"

echo "==> docker compose up -d (${SERVICES})"
# Layer .env.local over .env (later --env-file wins) so real secrets apply.
$SSH "pct exec ${CTID} -- bash -c 'cd ${TARGET} && ef=\"--env-file .env\"; [ -f .env.local ] && ef=\"\$ef --env-file .env.local\"; docker compose \$ef up -d ${SERVICES}'"
# Prometheus reads its config only at start; a bind-mount content change doesn't
# trigger a recreate, so restart it to pick up scrape-config edits.
echo "==> Reloading prometheus config"
$SSH "pct exec ${CTID} -- bash -c 'cd ${TARGET} && docker compose restart prometheus >/dev/null 2>&1'"
$SSH "pct exec ${CTID} -- bash -c 'cd ${TARGET} && docker compose ps'"

echo "==> Done. Grafana: http://10.10.0.40:3000 (admin / see .env)  ·  OTLP: 10.10.0.40:4317"
echo "    Point an emitter:  OTEL_EXPORTER_OTLP_ENDPOINT=http://10.10.0.40:4317"
