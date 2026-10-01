#!/usr/bin/env bash
#
# openbao-import-from-bws.sh — copy every secret in the Bitwarden SM "Homelab" project into
# OpenBao at secret/homelab/<KEY> (#609, step 2). Idempotent: an unchanged value is skipped,
# not re-written, so re-running does not mint a new KV version per secret.
#
# Prints key NAMES and counts only, never a value. Values travel bws → jq → curl on pipes;
# nothing is written to disk.
#
# Bitwarden SM stays the source of truth until secrets-sync.sh flips to OpenBao, so run this
# again after changing a secret in SM during the transition.
#
#   scripts/openbao-import-from-bws.sh            # import
#   scripts/openbao-import-from-bws.sh --dry-run  # list what would be written
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/openbao.sh
PROJECT_ID="ceb88092-7a26-4882-9e7b-b48a000a8f9a"   # SM "Homelab" project (as secrets-sync.sh)
DRY=0; [ "${1:-}" = "--dry-run" ] && DRY=1

die() { echo "✗ $*" >&2; exit 1; }
why="$(openbao_unavailable_reason)"; [ -z "$why" ] || die "OpenBao is $why"
openbao_login || die "AppRole login failed"
trap '_bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT

if [ -z "${BWS_ACCESS_TOKEN:-}" ]; then
  BWS_ACCESS_TOKEN="$(security find-generic-password -a bws -s homelab-bws-access-token -w 2>/dev/null || true)"
fi
[ -n "${BWS_ACCESS_TOKEN:-}" ] || die "no BWS_ACCESS_TOKEN (Keychain 'homelab-bws-access-token' or env)"
export BWS_ACCESS_TOKEN BWS_SERVER_URL="${BWS_SERVER_URL:-https://vault.bitwarden.eu}"
SM_JSON="$(bws secret list "$PROJECT_ID" -o json)"

dups="$(printf '%s' "$SM_JSON" | jq -r 'group_by(.key)[] | select(length>1) | .[0].key')"
[ -z "$dups" ] || die "SM has duplicate keys, refusing to guess which wins: $(echo $dups)"
bad="$(printf '%s' "$SM_JSON" | jq -r '.[].key | select(test("^[A-Za-z_][A-Za-z0-9_]*$")|not)')"
[ -z "$bad" ] || die "SM keys that are not env-var names (would not round-trip): $(echo $bad)"

written=0; same=0; total=0
for k in $(printf '%s' "$SM_JSON" | jq -r '.[].key' | sort); do
  total=$((total+1))
  if [ "$DRY" = 1 ]; then echo "  would write $k"; continue; fi
  rc=0; printf '%s' "$SM_JSON" | jq -j --arg k "$k" '.[]|select(.key==$k)|.value' | openbao_put "$k" || rc=$?
  case $rc in 0) written=$((written+1)); echo "  wrote     $k";; 3) same=$((same+1));; *) die "write failed for $k";; esac
done
echo "SM keys: $total   written: $written   unchanged: $same"
