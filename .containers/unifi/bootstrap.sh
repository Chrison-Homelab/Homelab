#!/usr/bin/env bash
# Reproducible bootstrap for the local UniFi OS Server test container.
#
# Turns a bare `docker compose up` into a ready-to-use controller WITHOUT the
# manual first-run wizard: brings the container up, completes first-run setup
# (creates a local admin) via /api/setup, verifies the LEGACY config API is
# reachable, and writes credentials to a gitignored credentials.env.
#
# Why username/password and not an API key:
#   • The LEGACY API (/proxy/network/api/s/<site>/rest/...) — the one with full
#     write coverage (port-forwards, firewall, networks/VLANs) — authenticates
#     with the classic controller session: POST /api/auth/login → TOKEN cookie +
#     X-CSRF-Token. That works here and on any UniFi OS gateway.
#   • The INTEGRATION API (/proxy/network/integration) needs an X-API-KEY, and
#     this community UniFi-OS-Server image exposes no scriptable key-mint endpoint
#     (the live Cloud Gateway's key is created in its cloud console). So the test
#     harness uses the session path; prod can still set UNIFI_API_KEY (the live
#     gateway accepts X-API-KEY on the legacy API too).
#
# Usage:
#   ./bootstrap.sh            # up (if needed) + setup (if needed) + verify + emit creds
#   ./bootstrap.sh --reset    # docker compose down -v first (wipe), then bootstrap fresh
#
# Override defaults via env: UOS_HOST, UNIFI_SETUP_NAME, UNIFI_USERNAME, UNIFI_PASSWORD.
set -euo pipefail
cd "$(dirname "$0")"

UOS_HOST="${UOS_HOST:-localhost:11443}"
SETUP_NAME="${UNIFI_SETUP_NAME:-Homelab Lab Controller}"
USERNAME="${UNIFI_USERNAME:-labadmin}"
PASSWORD="${UNIFI_PASSWORD:-LabAdmin!2026}"   # 8-64 chars (UniFi OS policy)
BASE="https://${UOS_HOST}"
CRED_FILE="credentials.env"

log() { printf '\033[36m▸\033[0m %s\n' "$*"; }
die() { printf '\033[31m✗ %s\033[0m\n' "$*" >&2; exit 1; }
api() { curl -sk --max-time 15 "$@"; }   # -k: the controller serves a self-signed cert

if [[ "${1:-}" == "--reset" ]]; then
  log "Wiping the container + data volume (--reset)…"
  docker compose down -v
fi

log "Bringing the container up (idempotent)…"
docker compose up -d >/dev/null

# 1) Wait for UniFi OS to answer /api/system (first boot re-downloads the installer).
log "Waiting for UniFi OS to come online…"
for i in $(seq 1 120); do
  sys="$(api "${BASE}/api/system" || true)"
  [[ -n "$sys" && "$sys" == *'"deviceState"'* ]] && break
  sleep 5
  [[ $i -eq 120 ]] && die "UniFi OS did not come online within 10 minutes"
done

# Already set up? The surest signal is that the admin can log in. /api/system's
# isSetup flag disappears once configured (it's only present pre-setup), so don't
# rely on it — just try the login.
login_works() {
  api -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
    -d "$(printf '{"username":"%s","password":"%s"}' "$USERNAME" "$PASSWORD")" \
    "${BASE}/api/auth/login" 2>/dev/null | grep -q '^200$'
}

# 2) Complete first-run setup if the device is unconfigured.
if login_works; then
  log "Device already set up (admin '${USERNAME}' can log in) — skipping first-run."
else
  log "Running first-run setup (creating local admin '${USERNAME}')…"
  # Wait until the setup endpoint is ready to accept input (device leaves notReady).
  for i in $(seq 1 60); do
    probe="$(api -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' "${BASE}/api/setup" || true)"
    [[ "$probe" == "400" || "$probe" == "500" ]] && break   # endpoint live, just wants a real body
    sleep 5
    [[ $i -eq 60 ]] && die "setup endpoint never became ready"
  done
  # The minimal accepted payload: console name + admin username + password.
  api -X POST -H 'Content-Type: application/json' \
    -d "$(printf '{"name":"%s","username":"%s","password":"%s"}' "$SETUP_NAME" "$USERNAME" "$PASSWORD")" \
    "${BASE}/api/setup" >/dev/null 2>&1 || true   # connection often resets as services restart — expected
  log "Setup submitted; waiting for the admin account to come up…"
fi

# 3) Verify: cookie login, then a real legacy-API call must return 200.
log "Verifying login + legacy API access…"
LOGIN_OK=
for i in $(seq 1 60); do
  hdr="$(api -X POST -H 'Content-Type: application/json' \
    -d "$(printf '{"username":"%s","password":"%s"}' "$USERNAME" "$PASSWORD")" \
    -D - -o /dev/null "${BASE}/api/auth/login" 2>/dev/null || true)"
  if grep -qi '^HTTP.* 200' <<<"$hdr"; then LOGIN_OK=1; break; fi
  sleep 5
done
[[ -n "$LOGIN_OK" ]] || die "login as '${USERNAME}' never succeeded"

cookiejar="$(mktemp)"; trap 'rm -f "$cookiejar"' EXIT
csrf="$(api -X POST -H 'Content-Type: application/json' \
  -d "$(printf '{"username":"%s","password":"%s"}' "$USERNAME" "$PASSWORD")" \
  -D - -c "$cookiejar" -o /dev/null "${BASE}/api/auth/login" \
  | grep -i '^x-csrf-token:' | tail -1 | tr -d '\r' | awk '{print $2}')"
code="$(api -b "$cookiejar" -H "x-csrf-token: ${csrf}" -o /dev/null -w '%{http_code}' \
  "${BASE}/proxy/network/api/s/default/rest/portforward")"
[[ "$code" == "200" ]] || die "legacy API check failed (rest/portforward → ${code})"
log "Legacy API reachable (rest/portforward → 200)."

# 4) Emit credentials (gitignored *.env) for UnifiSharp's legacy adapter + tests.
cat > "$CRED_FILE" <<EOF
# Generated by bootstrap.sh — local UniFi OS test container. GITIGNORED, throwaway creds.
# Source before running UnifiSharp's legacy-adapter tests:  set -a && . ./credentials.env && set +a
UNIFI_LEGACY_BASE_URL=${BASE}/proxy/network/api/s/default
UNIFI_BASE_URL=${BASE}/proxy/network/integration
UNIFI_USERNAME=${USERNAME}
UNIFI_PASSWORD=${PASSWORD}
UNIFI_VERIFY_TLS=false
EOF
log "Wrote ${CRED_FILE}:"
sed 's/^UNIFI_PASSWORD=.*/UNIFI_PASSWORD=<redacted>/' "$CRED_FILE" | sed 's/^/    /'
log "Done. Controller ready at ${BASE}/ (admin: ${USERNAME})."
