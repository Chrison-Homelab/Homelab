#!/usr/bin/env bash
#
# openbao-setup-oidc.sh — log in to OpenBao through authentik (#609, step 6).
# Run once by Christian, in a REAL terminal (bw unlock needs a TTY; the `!` prefix fails), after
# the Core deploy has applied the authentik blueprint's OpenBao provider.
#
#   export BW_SESSION="$(bw unlock --raw)"
#   scripts/openbao-setup-oidc.sh
#
# Creates (idempotent — re-running re-applies the same config):
#   auth  oidc/              discovery https://identity.chrison.dev/application/o/openbao/, client
#                            pair read from OpenBao itself (secret/homelab/AUTHENTIK_OPENBAO_*),
#                            so it never passes through this script's arguments or a file
#   role  oidc/role/admin    the default role: policy `admin`, granted only when the ID token's
#                            `groups` contains homelab-admins (authentik also only issues a token
#                            to that group: two independent checks). 1 h tokens, max 8 h.
# Then log in with:   web UI → method OIDC (role blank),   CLI → bao login -method=oidc
#
# The `christian` userpass login is NOT removed: it is the break-glass for when authentik is down
# (authentik's database is the thing you would most want the secrets for while fixing it).
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/openbao.sh

ITEM_ADMIN="OpenBao CT 3007 — admin login"
ISSUER="https://identity.chrison.dev/application/o/openbao/"
UI_CALLBACK="https://openbao.devops.chrison.internal:8200/ui/vault/auth/oidc/oidc/callback"
CLI_CALLBACK="http://localhost:8250/oidc/callback"
GROUP="homelab-admins"
die() { echo "✗ $*" >&2; exit 1; }
say() { echo "▸ $*"; }

command -v jq >/dev/null || die "jq not found"
[ "$(bw status 2>/dev/null | jq -r .status)" = unlocked ] \
  || die "Bitwarden is locked — run: export BW_SESSION=\"\$(bw unlock --raw)\""
why="$(OPENBAO_TOKEN_PRESET=x openbao_unavailable_reason)"; [ -z "$why" ] || die "OpenBao is $why"
curl -fsS -m 10 "${ISSUER}.well-known/openid-configuration" >/dev/null \
  || die "authentik has no OpenBao provider at $ISSUER yet — deploy Core first"

say "logging in as the admin user"
OPENBAO_TOKEN="$(bw get password "$ITEM_ADMIN" | jq -Rsc '{password:.}' \
  | OPENBAO_TOKEN='' _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/userpass/login/christian" \
  | jq -r '.auth.client_token // empty')"
[ -n "$OPENBAO_TOKEN" ] || die "admin login failed"
export OPENBAO_TOKEN
trap '_bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT

if ! _bao_curl "$OPENBAO_ADDR/v1/sys/auth" | jq -e '.data["oidc/"] // .["oidc/"]' >/dev/null; then
  say "enabling oidc auth at oidc/"
  printf '{"type":"oidc","description":"Human login via authentik (#609)"}' \
    | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/sys/auth/oidc" >/dev/null
fi

say "trusting authentik ($ISSUER)"
# The two KV reads are combined by jq on stdin: the client secret is never an argument.
{ _bao_curl "$OPENBAO_ADDR/v1/secret/data/$OPENBAO_KV_PREFIX/AUTHENTIK_OPENBAO_CLIENT_ID"
  _bao_curl "$OPENBAO_ADDR/v1/secret/data/$OPENBAO_KV_PREFIX/AUTHENTIK_OPENBAO_CLIENT_SECRET"; } \
  | jq -sc --arg iss "$ISSUER" '
      if (.[0].data.data.value // "") == "" or (.[1].data.data.value // "") == ""
      then error("AUTHENTIK_OPENBAO_CLIENT_ID/SECRET missing from OpenBao") else . end
      | {oidc_discovery_url: $iss, oidc_client_id: .[0].data.data.value,
         oidc_client_secret: .[1].data.data.value, default_role: "admin"}' \
  | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/oidc/config" >/dev/null

say "role admin (groups must contain $GROUP)"
jq -nc --arg ui "$UI_CALLBACK" --arg cli "$CLI_CALLBACK" --arg g "$GROUP" '{
  role_type: "oidc",
  user_claim: "sub",
  claim_mappings: {preferred_username: "username", email: "email"},
  groups_claim: "groups",
  bound_claims: {groups: [$g]},
  oidc_scopes: ["openid", "profile", "email"],
  allowed_redirect_uris: [$ui, $cli],
  token_policies: ["admin"],
  token_ttl: "1h",
  token_max_ttl: "8h"
}' | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/oidc/role/admin" >/dev/null

say "reading back"
_bao_curl "$OPENBAO_ADDR/v1/auth/oidc/config" | jq -c '.data | {oidc_discovery_url, default_role, oidc_client_id: (.oidc_client_id|length|tostring + " chars")}'
_bao_curl "$OPENBAO_ADDR/v1/auth/oidc/role/admin" | jq -c '.data | {bound_claims, token_policies, token_ttl, allowed_redirect_uris}'
# Proves discovery + client credentials end to end: OpenBao asks authentik for an auth URL.
url="$(jq -nc --arg r "$UI_CALLBACK" '{role:"admin", redirect_uri:$r}' \
  | OPENBAO_TOKEN='' _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/oidc/oidc/auth_url" | jq -r '.data.auth_url // empty')"
case "$url" in https://identity.chrison.dev/*) say "auth URL OK — log in: https://openbao.devops.chrison.internal:8200/ui/ → OIDC";;
  *) die "OpenBao could not build an authentik auth URL";; esac
