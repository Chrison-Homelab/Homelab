#!/usr/bin/env bash
#
# secrets-sync.sh — regenerate secrets.env from secrets.env.template, filling the
# blank (secret) keys from Bitwarden Secrets Manager (project "Homelab").
#
#   • Reads FROM Secrets Manager only (bws + Keychain token) — no vault unlock.
#   • Non-secret template lines pass through verbatim; blank keys are filled.
#   • Any blank template key NOT found in SM is left blank and reported LOUDLY
#     (this is the "half-filled" alarm — never silent).
#   • Output is written atomically at mode 600. No secret value is ever printed.
#
# Usage:
#   scripts/secrets-sync.sh                          # writes ./secrets.env
#   scripts/secrets-sync.sh /tmp/out                 # custom output path (for testing)
#   scripts/secrets-sync.sh <out> <template>         # custom output AND template — lets
#                                                    # other stacks reuse this engine, e.g.
#     scripts/secrets-sync.sh stacks/<Stack>/.env.local stacks/<Stack>/secrets.env.local.template
#
set -euo pipefail

PROJECT_ID="ceb88092-7a26-4882-9e7b-b48a000a8f9a"   # SM "Homelab" project
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$REPO_ROOT/secrets.env}"
TEMPLATE="${2:-$REPO_ROOT/secrets.env.template}"

[ -f "$TEMPLATE" ] || { echo "ERROR: $TEMPLATE not found" >&2; exit 1; }

# ── bws access token (Keychain, else pre-set env) + EU region ──
if [ -z "${BWS_ACCESS_TOKEN:-}" ]; then
  BWS_ACCESS_TOKEN="$(security find-generic-password -a bws -s homelab-bws-access-token -w 2>/dev/null || true)"
fi
[ -n "${BWS_ACCESS_TOKEN:-}" ] || { echo "ERROR: no BWS_ACCESS_TOKEN (Keychain 'homelab-bws-access-token' or env)" >&2; exit 1; }
export BWS_ACCESS_TOKEN
export BWS_SERVER_URL="${BWS_SERVER_URL:-https://vault.bitwarden.eu}"

# ── pull all SM secrets once (kept in memory, never written/printed) ──
SM_JSON="$(bws secret list "$PROJECT_ID" -o json)"
sm_value() { printf '%s' "$SM_JSON" | jq -r --arg k "$1" '.[]|select(.key==$k)|.value' | head -c 100000; }
sm_has()   { printf '%s' "$SM_JSON" | jq -e --arg k "$1" 'any(.[]; .key==$k)' >/dev/null; }

# single-quote a value for safe `set -a; . secrets.env` sourcing
shq() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }

TMP="$(mktemp "${TMPDIR:-/tmp}/secrets-sync.XXXXXX")"
trap 'rm -f "$TMP"' EXIT
chmod 600 "$TMP"

filled=0; passthrough=0; missing_keys=""
while IFS= read -r line || [ -n "$line" ]; do
  # match "<indent>KEY=" with EMPTY right-hand side → a fill target
  if printf '%s' "$line" | grep -qE '^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=$'; then
    pfx="$line"                                   # "<indent>KEY="
    key="$(printf '%s' "$pfx" | sed -E 's/^[[:space:]]*//; s/=$//')"
    if sm_has "$key"; then
      printf '%s%s\n' "$pfx" "$(shq "$(sm_value "$key")")" >> "$TMP"
      filled=$((filled+1))
    else
      printf '%s\n' "$line" >> "$TMP"             # leave blank
      missing_keys="$missing_keys $key"
    fi
  else
    printf '%s\n' "$line" >> "$TMP"
    passthrough=$((passthrough+1))
  fi
done < "$TEMPLATE"

# atomic install at mode 600
install -m 600 "$TMP" "$OUT"

echo "secrets.env written → $OUT"
echo "  filled from SM: $filled   passthrough lines: $passthrough"
if [ -n "$missing_keys" ]; then
  echo "  ⚠️  MISSING from SM (left blank):$missing_keys" >&2
  echo "     → add them to the SM 'Homelab' project, then re-run." >&2
  exit 2
fi
