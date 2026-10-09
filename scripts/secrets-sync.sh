#!/usr/bin/env bash
#
# secrets-sync.sh — regenerate secrets.env from secrets.env.template, filling the
# blank (secret) keys from the homelab secrets store.
#
#   • SOURCE: OpenBao (DevOps CT 3007, secret/homelab/<KEY>) via this workstation's AppRole in
#     the Keychain, or a token handed in as OPENBAO_TOKEN_PRESET (CI). OpenBao is the ONLY store
#     since #609 finished: Bitwarden SM was frozen as a cold copy on 2026-10-03 and is no longer
#     read. If OpenBao is unreachable or SEALED this fails and says which; there is no fallback.
#   • Non-secret template lines pass through verbatim; blank keys are filled.
#   • Any blank template key NOT found in the store is left blank and reported LOUDLY
#     (this is the "half-filled" alarm — never silent).
#   • Output is written atomically at mode 600. No secret value is ever printed.
#
# Adding a secret: scripts/openbao-set.sh KEY (prompts, hidden), then `KEY=` in the template.
#
# Usage:
#   scripts/secrets-sync.sh                          # writes ./secrets.env
#   scripts/secrets-sync.sh /tmp/out                 # custom output path (for testing)
#   scripts/secrets-sync.sh <out> <template>         # custom output AND template — lets
#                                                    # other stacks reuse this engine, e.g.
#     scripts/secrets-sync.sh stacks/<Stack>/.env.local stacks/<Stack>/secrets.env.local.template
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$REPO_ROOT/secrets.env}"; TEMPLATE="${2:-$REPO_ROOT/secrets.env.template}"

[ -f "$TEMPLATE" ] || { echo "ERROR: $TEMPLATE not found" >&2; exit 1; }
. "$REPO_ROOT/scripts/lib/openbao.sh"

why="$(openbao_unavailable_reason)"; [ -z "$why" ] || { echo "ERROR: OpenBao is $why" >&2; exit 1; }
openbao_login || { echo "ERROR: OpenBao login failed" >&2; exit 1; }
MAP="$(openbao_dump)" || { echo "ERROR: reading OpenBao failed" >&2; exit 1; }
# A preset token belongs to the caller (CI revokes it itself); only our own login is revoked here.
[ -n "${OPENBAO_TOKEN_PRESET:-}" ] || _bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true
SRC=OpenBao
sm_value() { printf '%s' "$MAP" | jq -r --arg k "$1" '.[$k]' | head -c 100000; }
sm_has()   { printf '%s' "$MAP" | jq -e --arg k "$1" 'has($k)' >/dev/null; }

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
echo "  filled from $SRC: $filled   passthrough lines: $passthrough"
if [ -n "$missing_keys" ]; then
  echo "  ⚠️  MISSING from $SRC (left blank):$missing_keys" >&2
  echo "     → add them with scripts/openbao-set.sh KEY, and re-run." >&2
  exit 2
fi
