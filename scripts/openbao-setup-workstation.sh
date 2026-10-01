#!/usr/bin/env bash
#
# openbao-setup-workstation.sh — give THIS workstation its own OpenBao login (#609, step 2).
# Run once, by Christian, with the Bitwarden CLI unlocked. Needs the admin login the bootstrap
# stored ("OpenBao CT 3007 — admin login"); reads it from Bitwarden so it is never typed.
#
# Creates (idempotent; re-running rotates the secret_id):
#   policy  homelab-workstation   create/read/update on secret/homelab/*, list its metadata.
#                                 NO delete, nothing outside secret/homelab/.
#   auth    approle/              (enabled if absent)
#   role    approle/role/workstation
#             token_ttl 15m · token_max_ttl 1h · secret_id_ttl 0 (does not expire, like the BWS
#             token it replaces) · bound to 10.0.0.0/8, so a leaked secret_id is useless off-LAN
# and stores role_id + secret_id in the macOS Keychain (services homelab-openbao-role-id /
# homelab-openbao-secret-id), which is where scripts/secrets-sync.sh looks for them.
#
# Nothing secret is printed or put in argv: bodies go on stdin, the admin token is a header read
# from an fd, and the Keychain write goes through `security -i` on stdin.
#
#   export BW_SESSION="$(bw unlock --raw)"
#   scripts/openbao-setup-workstation.sh
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/openbao.sh

ITEM_ADMIN="OpenBao CT 3007 — admin login"
die() { echo "✗ $*" >&2; exit 1; }
say() { echo "▸ $*"; }

command -v jq >/dev/null || die "jq not found"
[ "$(bw status 2>/dev/null | jq -r .status)" = unlocked ] \
  || die "Bitwarden is locked — run: export BW_SESSION=\"\$(bw unlock --raw)\""

# Only "no AppRole in Keychain" is acceptable here — that is what this script fixes.
why="$(openbao_unavailable_reason)"
case "$why" in ""|"no AppRole in Keychain"*) ;; *) die "OpenBao is $why";; esac

say "logging in as the admin user"
OPENBAO_TOKEN="$(bw get password "$ITEM_ADMIN" | jq -Rsc '{password:.}' \
  | OPENBAO_TOKEN='' _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/userpass/login/christian" \
  | jq -r '.auth.client_token // empty')"
[ -n "$OPENBAO_TOKEN" ] || die "admin login failed"
export OPENBAO_TOKEN
ADMIN_TOKEN="$OPENBAO_TOKEN"
# Revoke the ADMIN token on any exit, whichever token happens to be current by then.
trap 'OPENBAO_TOKEN="$ADMIN_TOKEN" _bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT

say "policy homelab-workstation"
jq -nc --arg p "$OPENBAO_KV_PREFIX" '{policy: (
  "path \"secret/data/\($p)/*\"     { capabilities = [\"create\",\"read\",\"update\"] }\n" +
  "path \"secret/metadata/\($p)\"   { capabilities = [\"list\",\"read\"] }\n" +
  "path \"secret/metadata/\($p)/*\" { capabilities = [\"list\",\"read\"] }\n")}' \
  | _bao_curl -X PUT --data @- "$OPENBAO_ADDR/v1/sys/policies/acl/homelab-workstation" >/dev/null

if ! _bao_curl "$OPENBAO_ADDR/v1/sys/auth" | jq -e '.data["approle/"] // .["approle/"]' >/dev/null; then
  say "enabling approle auth"
  printf '{"type":"approle"}' | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/sys/auth/approle" >/dev/null
fi

say "role approle/workstation"
printf '%s' '{"token_policies":["homelab-workstation"],"token_ttl":"15m","token_max_ttl":"1h",
  "secret_id_ttl":"0","token_bound_cidrs":["10.0.0.0/8"],"secret_id_bound_cidrs":["10.0.0.0/8"]}' \
  | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/approle/role/workstation" >/dev/null

role_id="$(_bao_curl "$OPENBAO_ADDR/v1/auth/approle/role/workstation/role-id" | jq -r .data.role_id)"
secret_id="$(_bao_curl -X POST "$OPENBAO_ADDR/v1/auth/approle/role/workstation/secret-id" | jq -r .data.secret_id)"
[ -n "$role_id" ] && [ -n "$secret_id" ] || die "could not read role_id / mint secret_id"

say "storing role_id + secret_id in the Keychain"
printf 'add-generic-password -U -a openbao -s %s -w %s\nadd-generic-password -U -a openbao -s %s -w %s\n' \
  "$OPENBAO_KC_ROLE" "$role_id" "$OPENBAO_KC_SECRET" "$secret_id" | security -i >/dev/null
unset role_id secret_id

say "verifying the workstation login"
unset OPENBAO_TOKEN
openbao_login || die "AppRole login with the stored credentials failed"
pol="$(_bao_curl "$OPENBAO_ADDR/v1/auth/token/lookup-self" | jq -c '.data.policies')"
case "$pol" in *homelab-workstation*) ;; *) die "token has unexpected policies: $pol";; esac
_bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true
say "done — this workstation can now read/write secret/$OPENBAO_KV_PREFIX/* (policies: $pol)"
