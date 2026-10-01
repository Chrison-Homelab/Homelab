# shellcheck shell=bash
# scripts/lib/openbao.sh — shared helpers for talking to the homelab OpenBao (DevOps CT 3007, #609).
# Sourced, not executed. bash 3.2 compatible (macOS /usr/bin/env bash).
#
# TRUST: TLS is verified against scripts/openbao-ca.pem, the CT's own self-signed certificate
# (public; committed). Never -k. The cert was re-issued at bootstrap with a SAN of
# openbao.devops.chrison.internal + 10.10.30.7, valid 3 years (to 2029-10-01).
#
# AUTH: AppRole `workstation`. Its role_id and secret_id live in the macOS Keychain, set once by
# scripts/openbao-setup-workstation.sh, exactly like the BWS token before them. A login mints a
# short-lived token (15 min). Tokens and values never go in argv: curl reads the token header
# from a file descriptor (-H @<(…)) and request bodies from stdin (--data @-).

OPENBAO_ADDR="${OPENBAO_ADDR:-https://openbao.devops.chrison.internal:8200}"
OPENBAO_CACERT="${OPENBAO_CACERT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/openbao-ca.pem}"
OPENBAO_KV_PREFIX="${OPENBAO_KV_PREFIX:-homelab}"   # secret/<prefix>/<KEY>, one entry per key, field "value"
OPENBAO_KC_ROLE="homelab-openbao-role-id"
OPENBAO_KC_SECRET="homelab-openbao-secret-id"

_bao_kc() { security find-generic-password -a openbao -s "$1" -w 2>/dev/null || true; }

# curl wrapper: verified TLS, token header (if any) from an fd, body (if any) on stdin.
_bao_curl() {
  if [ -n "${OPENBAO_TOKEN:-}" ]; then
    curl -fsS --cacert "$OPENBAO_CACERT" -H @<(printf 'X-Vault-Token: %s\n' "$OPENBAO_TOKEN") "$@"
  else
    curl -fsS --cacert "$OPENBAO_CACERT" "$@"
  fi
}

# Why OpenBao is unusable, or nothing if it is fine. Distinguishes the cases that need a
# different human action: unreachable, SEALED (someone has to unseal it), not set up here.
openbao_unavailable_reason() {
  local h
  h="$(curl -sS --cacert "$OPENBAO_CACERT" -m 5 "$OPENBAO_ADDR/v1/sys/seal-status" 2>/dev/null)" \
    || { echo "unreachable at $OPENBAO_ADDR"; return; }
  [ "$(printf '%s' "$h" | jq -r .initialized 2>/dev/null)" = true ] || { echo "not initialised"; return; }
  [ "$(printf '%s' "$h" | jq -r .sealed 2>/dev/null)" = false ] \
    || { echo "SEALED — unseal on CT 3007 (2 shares from Bitwarden)"; return; }
  [ -n "$(_bao_kc "$OPENBAO_KC_ROLE")" ] && [ -n "$(_bao_kc "$OPENBAO_KC_SECRET")" ] \
    || { echo "no AppRole in Keychain — run scripts/openbao-setup-workstation.sh"; return; }
}

# AppRole login; exports OPENBAO_TOKEN. Returns non-zero (no output) on failure.
openbao_login() {
  local body
  # printf is a builtin, so the secret_id is never in a process's argv. Both ids are UUIDs.
  body="$(printf '{"role_id":"%s","secret_id":"%s"}' "$(_bao_kc "$OPENBAO_KC_ROLE")" "$(_bao_kc "$OPENBAO_KC_SECRET")")"
  OPENBAO_TOKEN="$(printf '%s' "$body" | OPENBAO_TOKEN='' _bao_curl -X POST --data @- \
    "$OPENBAO_ADDR/v1/auth/approle/login" | jq -r '.auth.client_token // empty')" || return 1
  [ -n "$OPENBAO_TOKEN" ] || return 1
  export OPENBAO_TOKEN
}

# All secrets under the prefix as ONE JSON object {KEY: value}, printed to stdout (callers keep it
# in memory, never on disk). An empty prefix yields {}.
openbao_dump() {
  local keys k out="{}"
  keys="$(_bao_curl -X LIST "$OPENBAO_ADDR/v1/secret/metadata/$OPENBAO_KV_PREFIX" 2>/dev/null \
    | jq -r '.data.keys[]? | select(endswith("/")|not)')" || keys=""
  # Accumulator and response are streamed into `jq -s` on stdin, never passed as --arg/--argjson:
  # an argument is visible in the process list, a pipe is not. Only the key NAME is an argument.
  for k in $keys; do
    out="$({ printf '%s' "$out"; _bao_curl "$OPENBAO_ADDR/v1/secret/data/$OPENBAO_KV_PREFIX/$k"; } \
      | jq -sc --arg k "$k" '.[0] + {($k): .[1].data.data.value}')" || return 1
  done
  printf '%s' "$out"
}

# Write one key, value on stdin. No-op (returns 3) if the current version already holds it, so a
# re-run does not mint a new KV version for every unchanged secret.
openbao_put() {
  local k="$1" v cur
  v="$(cat)"
  cur="$(_bao_curl "$OPENBAO_ADDR/v1/secret/data/$OPENBAO_KV_PREFIX/$k" 2>/dev/null | jq -r '.data.data.value // empty')" || cur=""
  [ "$cur" = "$v" ] && [ -n "$cur" ] && return 3
  printf '%s' "$v" | jq -Rsc '{data:{value:.}}' | _bao_curl -X POST --data @- \
    "$OPENBAO_ADDR/v1/secret/data/$OPENBAO_KV_PREFIX/$k" >/dev/null
}
