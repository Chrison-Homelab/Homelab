#!/usr/bin/env bash
#
# secrets-sync.sh — regenerate secrets.env from secrets.env.template, filling the
# blank (secret) keys from the homelab secrets store.
#
#   • SOURCE (#609): OpenBao (DevOps CT 3007, secret/homelab/<KEY>) via this workstation's
#     AppRole in the Keychain; FALLS BACK to Bitwarden Secrets Manager (project "Homelab") if
#     OpenBao is unreachable, SEALED or not set up here, and says why, loudly. Force one with
#     SECRETS_SOURCE=openbao|bws. During the trial both hold the same values
#     (scripts/openbao-import-from-bws.sh copies SM → OpenBao).
#   • Non-secret template lines pass through verbatim; blank keys are filled.
#   • Any blank template key NOT found in the source is left blank and reported LOUDLY
#     (this is the "half-filled" alarm — never silent).
#   • Output is written atomically at mode 600. No secret value is ever printed.
#
# Usage:
#   scripts/secrets-sync.sh                          # writes ./secrets.env
#   scripts/secrets-sync.sh /tmp/out                 # custom output path (for testing)
#   scripts/secrets-sync.sh <out> <template>         # custom output AND template — lets
#                                                    # other stacks reuse this engine, e.g.
#     scripts/secrets-sync.sh stacks/<Stack>/.env.local stacks/<Stack>/secrets.env.local.template
#   scripts/secrets-sync.sh --compare [template]     # fill from BOTH sources, write nothing,
#                                                    # report every key that differs (names only)
#
set -euo pipefail

PROJECT_ID="ceb88092-7a26-4882-9e7b-b48a000a8f9a"   # SM "Homelab" project
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPARE=0; [ "${1:-}" = "--compare" ] && { COMPARE=1; shift; }
if [ "$COMPARE" = 1 ]; then OUT=""; TEMPLATE="${1:-$REPO_ROOT/secrets.env.template}"
else OUT="${1:-$REPO_ROOT/secrets.env}"; TEMPLATE="${2:-$REPO_ROOT/secrets.env.template}"; fi

[ -f "$TEMPLATE" ] || { echo "ERROR: $TEMPLATE not found" >&2; exit 1; }
. "$REPO_ROOT/scripts/lib/openbao.sh"

# ── each source yields ONE JSON object {KEY: value}, kept in memory, never written/printed ──
bws_map() {
  if [ -z "${BWS_ACCESS_TOKEN:-}" ]; then
    BWS_ACCESS_TOKEN="$(security find-generic-password -a bws -s homelab-bws-access-token -w 2>/dev/null || true)"
  fi
  [ -n "${BWS_ACCESS_TOKEN:-}" ] || { echo "ERROR: no BWS_ACCESS_TOKEN (Keychain 'homelab-bws-access-token' or env)" >&2; return 1; }
  export BWS_ACCESS_TOKEN
  export BWS_SERVER_URL="${BWS_SERVER_URL:-https://vault.bitwarden.eu}"
  # First occurrence wins, matching the old `select | head` behaviour on a duplicated key.
  bws secret list "$PROJECT_ID" -o json | jq -c 'reduce .[] as $s ({}; if has($s.key) then . else . + {($s.key): $s.value} end)'
}
openbao_map() {
  openbao_login || { echo "ERROR: OpenBao AppRole login failed" >&2; return 1; }
  local m rc=0; m="$(openbao_dump)" || rc=$?
  # A preset token belongs to the caller (CI revokes it itself); only our own login is revoked here.
  [ -n "${OPENBAO_TOKEN_PRESET:-}" ] || _bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true
  [ "$rc" = 0 ] && printf '%s' "$m"
}

SOURCE="${SECRETS_SOURCE:-auto}"
if [ "$COMPARE" = 1 ]; then
  why="$(openbao_unavailable_reason)"; [ -z "$why" ] || { echo "ERROR: --compare needs OpenBao, which is $why" >&2; exit 1; }
  A="$(openbao_map)"; B="$(bws_map)"
  diff_keys="$( { printf '%s\n' "$A"; printf '%s\n' "$B"; } | jq -rs '
    .[0] as $a | .[1] as $b | ($a|keys) + ($b|keys) | unique[]
    | select($a[.] != $b[.])
    | . + (if ($a|has(.))|not then "  (missing from OpenBao)" elif ($b|has(.))|not then "  (only in OpenBao)" else "  (value differs)" end)')"
  used="$(grep -E '^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=$' "$TEMPLATE" | sed -E 's/^[[:space:]]*//; s/=$//' | sort -u)"
  echo "OpenBao keys: $(printf '%s' "$A" | jq length)   SM keys: $(printf '%s' "$B" | jq length)   template fill targets: $(echo "$used" | grep -c .)"
  if [ -z "$diff_keys" ]; then echo "✓ identical"; exit 0; fi
  # "only in OpenBao" is the direction of travel (OpenBao is primary, SM is being retired), e.g.
  # host secrets read via openbao-exec (#609 step 5) that never had an SM copy. Reported, not failed.
  if ! printf '%s\n' "$diff_keys" | grep -qv '(only in OpenBao)$'; then
    echo "✓ identical apart from OpenBao-only keys:"; echo "$diff_keys" | sed 's/^/    /'; exit 0
  fi
  echo "✗ differences (key names only):"; echo "$diff_keys" | sed 's/^/    /'; exit 3
fi

case "$SOURCE" in
  openbao) why="$(openbao_unavailable_reason)"; [ -z "$why" ] || { echo "ERROR: SECRETS_SOURCE=openbao but OpenBao is $why" >&2; exit 1; }
           MAP="$(openbao_map)"; SRC=OpenBao ;;
  bws)     MAP="$(bws_map)"; SRC="Bitwarden SM" ;;
  auto)    why="$(openbao_unavailable_reason)"
           if [ -z "$why" ] && MAP="$(openbao_map)"; then SRC=OpenBao
           else
             echo "⚠️  OpenBao not used (${why:-login or read failed}) — FALLING BACK to Bitwarden SM" >&2
             MAP="$(bws_map)"; SRC="Bitwarden SM"
           fi ;;
  *) echo "ERROR: SECRETS_SOURCE must be openbao, bws or auto" >&2; exit 1 ;;
esac
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
  echo "     → add them to the SM 'Homelab' project (then scripts/openbao-import-from-bws.sh), and re-run." >&2
  exit 2
fi
