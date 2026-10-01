#!/usr/bin/env bash
#
# openbao-setup-host.sh — give one host read access to its own secrets (#609, step 5).
# Run by Christian, with the Bitwarden CLI unlocked (reads the admin login from it).
#
#   export BW_SESSION="$(bw unlock --raw)"
#   scripts/openbao-setup-host.sh hpe-01
#
# For <host> as listed in scripts/openbao-hosts.conf (idempotent; re-running rotates the secret-id):
#   policy  host-<host>          READ on exactly the listed secret/homelab/<KEY>s. No list, no write.
#   role    approle/host-<host>  tokens 5 min (max 10), token AND secret-id bound to the host's CIDR
#   on the host (over ssh, values on stdin only):
#     /etc/openbao-client/approle.json  role_id + a fresh secret_id   0600 root
#     /etc/openbao-client/ca.pem        scripts/openbao-ca.pem
#     /usr/local/bin/openbao-exec       scripts/openbao-exec
#   then proves it: the host reads each listed key through openbao-exec (values never shown).
#   Only once that works are the role's older secret-ids destroyed, so a failed run locks nothing out.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/openbao.sh

ITEM_ADMIN="OpenBao CT 3007 — admin login"
die() { echo "✗ $*" >&2; exit 1; }
say() { echo "▸ $*"; }

HOST="${1:-}"; [ -n "$HOST" ] || die "usage: $0 <host>   (a host listed in scripts/openbao-hosts.conf)"
line="$(grep -E "^$HOST[[:space:]]" scripts/openbao-hosts.conf || true)"
[ -n "$line" ] || die "$HOST is not in scripts/openbao-hosts.conf"
read -r _ CIDR KEYS <<< "$line"
[ -n "$KEYS" ] || die "$HOST lists no keys"
ROLE="host-$HOST"; POLICY="host-$HOST"; SSH_TARGET="root@$HOST.homelab.chrison.internal"

command -v jq >/dev/null || die "jq not found"
[ "$(bw status 2>/dev/null | jq -r .status)" = unlocked ] \
  || die "Bitwarden is locked — run: export BW_SESSION=\"\$(bw unlock --raw)\""
why="$(OPENBAO_TOKEN_PRESET=x openbao_unavailable_reason)"; [ -z "$why" ] || die "OpenBao is $why"
ssh -o BatchMode=yes "$SSH_TARGET" true 2>/dev/null || die "cannot ssh to $SSH_TARGET"

say "logging in as the admin user"
OPENBAO_TOKEN="$(bw get password "$ITEM_ADMIN" | jq -Rsc '{password:.}' \
  | OPENBAO_TOKEN='' _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/userpass/login/christian" \
  | jq -r '.auth.client_token // empty')"
[ -n "$OPENBAO_TOKEN" ] || die "admin login failed"
export OPENBAO_TOKEN
trap '_bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT

say "policy $POLICY: read $KEYS"
for k in $KEYS; do printf 'path "secret/data/%s/%s" { capabilities = ["read"] }\n' "$OPENBAO_KV_PREFIX" "$k"; done \
  | jq -Rsc '{policy: .}' | _bao_curl -X PUT --data @- "$OPENBAO_ADDR/v1/sys/policies/acl/$POLICY" >/dev/null

say "role approle/$ROLE (bound to $CIDR)"
jq -nc --arg pol "$POLICY" --arg cidr "$CIDR" '{
  token_policies: [$pol], token_ttl: "5m", token_max_ttl: "10m",
  token_bound_cidrs: [$cidr], secret_id_bound_cidrs: [$cidr],
  secret_id_ttl: "0", secret_id_num_uses: 0
}' | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/approle/role/$ROLE" >/dev/null

old="$(_bao_curl -X LIST "$OPENBAO_ADDR/v1/auth/approle/role/$ROLE/secret-id" 2>/dev/null | jq -r '.data.keys[]?' || true)"

say "minting a secret-id and installing the client on $HOST"
# role_id and secret_id are combined by jq reading the two responses on stdin, and go to the host
# on ssh's stdin: never an argument, never a local file.
{ _bao_curl "$OPENBAO_ADDR/v1/auth/approle/role/$ROLE/role-id"
  _bao_curl -X POST "$OPENBAO_ADDR/v1/auth/approle/role/$ROLE/secret-id"; } \
  | jq -sc '{role_id: .[0].data.role_id, secret_id: .[1].data.secret_id}' \
  | ssh -o BatchMode=yes "$SSH_TARGET" 'umask 077; mkdir -p /etc/openbao-client \
      && cat > /etc/openbao-client/approle.json.new && mv /etc/openbao-client/approle.json.new /etc/openbao-client/approle.json'
ssh -o BatchMode=yes "$SSH_TARGET" 'cat > /etc/openbao-client/ca.pem' < scripts/openbao-ca.pem
ssh -o BatchMode=yes "$SSH_TARGET" 'cat > /usr/local/bin/openbao-exec.new && chmod 0755 /usr/local/bin/openbao-exec.new \
  && mv /usr/local/bin/openbao-exec.new /usr/local/bin/openbao-exec' < scripts/openbao-exec

say "proving $HOST can read its keys"
# The host prints only "KEY=" for each key it received (grep -o cuts the value off there).
# shellcheck disable=SC2029  # KEYS are names, expanded locally on purpose
got="$(ssh -o BatchMode=yes "$SSH_TARGET" \
  "/usr/local/bin/openbao-exec ${KEYS// /,} -- /bin/sh -c 'env | grep -oE \"^(${KEYS// /|})=\"'")" \
  || die "the host could not read its keys (see above); its older secret-ids were left in place"
printf '%s\n' "$got" | sed 's/=$//; s/^/  ✓ /'
[ "$(printf '%s\n' "$got" | grep -c .)" = "$(echo $KEYS | wc -w | tr -d ' ')" ] \
  || die "the host received fewer keys than listed; its older secret-ids were left in place"

if [ -n "$old" ]; then
  say "destroying $(echo "$old" | wc -l | tr -d ' ') older secret-id(s)"
  for a in $old; do
    jq -nc --arg a "$a" '{secret_id_accessor: $a}' \
      | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/approle/role/$ROLE/secret-id-accessor/destroy" >/dev/null
  done
fi
say "done — units on $HOST can now use: openbao-exec KEY[,KEY...] -- <command>"
