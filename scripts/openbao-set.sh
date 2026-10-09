#!/usr/bin/env bash
#
# openbao-set.sh — add or change one homelab secret in OpenBao (secret/homelab/<KEY>, field value).
# Replaces "add it to Bitwarden SM, then openbao-import-from-bws.sh": OpenBao is the only store.
#
#   scripts/openbao-set.sh KEY              # prompts for the value (hidden)
#   printf %s "$v" | scripts/openbao-set.sh KEY   # or takes it on stdin, never as an argument
#
# Uses this workstation's AppRole (create/read/update, no delete). An unchanged value writes no new
# KV version. Then add `KEY=` to secrets.env.template so secrets-sync and CI fill it.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/openbao.sh

KEY="${1:-}"
[[ "$KEY" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || { echo "usage: $0 KEY   (KEY like FOO_API_KEY)" >&2; exit 1; }
why="$(openbao_unavailable_reason)"; [ -z "$why" ] || { echo "✗ OpenBao is $why" >&2; exit 1; }

if [ -t 0 ]; then
  read -r -s -p "value for $KEY (hidden): " v; echo
else
  v="$(cat)"
fi
[ -n "$v" ] || { echo "✗ empty value, nothing written" >&2; exit 1; }

openbao_login || { echo "✗ OpenBao login failed" >&2; exit 1; }
trap '_bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT
rc=0; printf '%s' "$v" | openbao_put "$KEY" || rc=$?
unset v
case "$rc" in
  0) echo "✓ $KEY written";;
  3) echo "✓ $KEY unchanged";;
  *) echo "✗ writing $KEY failed" >&2; exit 1;;
esac
grep -qE "^[[:space:]]*$KEY=" secrets.env.template || echo "  → now add '$KEY=' to secrets.env.template"
