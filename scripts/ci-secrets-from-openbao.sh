#!/usr/bin/env bash
#
# ci-secrets-from-openbao.sh — give a GitHub Actions job its secrets from OpenBao (#609, step 3).
# Runs in _deploy-stack.yml on the self-hosted homelab runner. Replaces the hand-kept list of
# `KEY: ${{ secrets.KEY }}` passthroughs, which was the fourth copy of every secret NAME and the
# source of #578/#579 (and NTFY_FREELEECH_TOKEN, reported done and not done).
#
#   1. GitHub OIDC token (audience openbao-homelab) → OpenBao JWT login, role `deploy`.
#      OpenBao binds that role to THIS repo, THIS workflow file and a self-hosted runner on the LAN.
#   2. scripts/secrets-sync.sh with that token: the SAME template + store the laptop uses, so CI
#      and laptop cannot disagree about a value or a name.
#   3. Every resulting KEY=value is MASKED and exported to $GITHUB_ENV for the later steps, which is
#      the interface the engine already reads on a runner (process env, no secrets.env file).
#      A key the workflow already sets itself (node addresses, the SSH key path) is left alone:
#      workflow constants win.
#
# ⚠ THIS REPO IS PUBLIC, SO ITS ACTIONS LOGS ARE PUBLIC. GitHub masks only its own `secrets.*`; a
#   value fetched at runtime is printed in clear unless it is registered with ::add-mask::. Every
#   line of every value is masked BEFORE it is written anywhere. Never `set -x` in here.
#
# ROLE / KEYS MODE: OPENBAO_ROLE picks the JWT role (default `deploy`). With OPENBAO_KEYS set (space
# separated), step 2 is replaced by reading EXACTLY those keys: from OpenBao where the store has
# them, else the template's committed literal (PROXMOX_BASE_URL and friends are not secrets). That is
# for workflows whose role may read only a handful of keys, e.g. discover-drift.yml → role `discover`.
#
# Outcome contract with the workflow:
#   exit 0 + "fallback=false" in $GITHUB_OUTPUT → secrets exported from OpenBao
#   exit 0 + "fallback=true"                    → OpenBao UNAVAILABLE (unreachable, sealed, no
#                                                 OIDC permission, login refused): the workflow
#                                                 falls back to GitHub's own secrets, loudly
#   exit ≠ 0                                    → OpenBao answered but something is WRONG (e.g. a
#                                                 template key missing from the store). Fails the
#                                                 run: falling back would hide exactly the drift
#                                                 this exists to remove.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/openbao.sh

: "${GITHUB_ENV:?not running under GitHub Actions}"
: "${GITHUB_OUTPUT:?not running under GitHub Actions}"
AUDIENCE="openbao-homelab"
ROLE="${OPENBAO_ROLE:-deploy}"
MOUNT="jwt-github"

fallback() { echo "::warning title=OpenBao unavailable::$1 — this run uses the GitHub-mirrored secrets"; echo "fallback=true" >> "$GITHUB_OUTPUT"; exit 0; }

[ -n "${ACTIONS_ID_TOKEN_REQUEST_URL:-}" ] || fallback "no OIDC token available (the calling job needs permissions: id-token: write)"
why="$(OPENBAO_TOKEN_PRESET=x openbao_unavailable_reason)"; [ -z "$why" ] || fallback "OpenBao is $why"

jwt="$(curl -fsS -H @<(printf 'Authorization: bearer %s\n' "$ACTIONS_ID_TOKEN_REQUEST_TOKEN") \
  "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=$AUDIENCE" | jq -r '.value // empty')" || jwt=""
[ -n "$jwt" ] || fallback "could not obtain a GitHub OIDC token"
echo "::add-mask::$jwt"

tok="$(printf '{"role":"%s","jwt":"%s"}' "$ROLE" "$jwt" \
  | curl -fsS --cacert "$OPENBAO_CACERT" -X POST --data @- "$OPENBAO_ADDR/v1/auth/$MOUNT/login" \
  | jq -r '.auth.client_token // empty')" || tok=""
unset jwt
[ -n "$tok" ] || fallback "JWT login to OpenBao was refused (role '$ROLE' on auth/$MOUNT not set up yet?)"
echo "::add-mask::$tok"
trap 'OPENBAO_TOKEN="$tok" _bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT

# Masks every line of $2, then appends KEY<<delim/value to $GITHUB_ENV.
export_masked() {
  local line d
  # Mask EVERY line first: GitHub matches masks per line, so a multi-line value masked as one
  # string would leak its lines individually.
  while IFS= read -r line || [ -n "$line" ]; do [ -n "$line" ] && echo "::add-mask::$line"; done <<< "$2"
  d="EOF_$(openssl rand -hex 12)"
  printf '%s<<%s\n%s\n%s\n' "$1" "$d" "$2" "$d" >> "$GITHUB_ENV"
}

if [ -n "${OPENBAO_KEYS:-}" ]; then
  n=0
  for k in $OPENBAO_KEYS; do
    [ -n "${!k+x}" ] && continue                                  # workflow constants win
    v="$(OPENBAO_TOKEN="$tok" _bao_curl "$OPENBAO_ADDR/v1/secret/data/$OPENBAO_KV_PREFIX/$k" 2>/dev/null \
      | jq -r '.data.data.value // empty')" || v=""
    if [ -z "$v" ]; then
      # Not in the store: the template's literal, quotes stripped the way a shell would.
      v="$(sed -nE "s/^[[:space:]]*$k=(.+)$/\1/p" secrets.env.template | head -1 | sed -E "s/^\"(.*)\"$/\1/; s/^'(.*)'$/\1/")"
    fi
    [ -n "$v" ] || { echo "::error::$k is neither readable in OpenBao (role '$ROLE') nor a literal in secrets.env.template"; exit 1; }
    export_masked "$k" "$v"; n=$((n+1))
  done
  echo "fallback=false" >> "$GITHUB_OUTPUT"
  echo "OpenBao (role $ROLE): exported $n key(s): $OPENBAO_KEYS"
  exit 0
fi

OUT="$(mktemp "${RUNNER_TEMP:-/tmp}/secrets-env.XXXXXX")"; chmod 600 "$OUT"
trap 'rm -f "$OUT"; OPENBAO_TOKEN="$tok" _bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT
# From here a failure is a real fault, not unavailability: let it fail the run.
SECRETS_SOURCE=openbao OPENBAO_TOKEN_PRESET="$tok" scripts/secrets-sync.sh "$OUT" secrets.env.template

# Every KEY the template declares (filled and passthrough alike), in template order.
keys="$(grep -oE '^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=' secrets.env.template | sed -E 's/^[[:space:]]*//; s/=$//' | awk '!seen[$0]++')"
# Which of them the workflow already sets — those are constants and win. Captured BEFORE sourcing.
preset=""; for k in $keys; do [ -n "${!k+x}" ] && preset="$preset $k"; done

before=$(grep -c '<<EOF_' "$GITHUB_ENV" 2>/dev/null || true)
(
  set -a; . "$OUT"; set +a
  for k in $keys; do
    case " $preset " in *" $k "*) continue;; esac
    case "$k" in PATH|HOME|SHELL|USER|IFS|LD_*|GITHUB_*|RUNNER_*|ACTIONS_*) echo "::error::template key $k would override the job environment"; exit 1;; esac
    v="${!k-}"; [ -n "$v" ] || continue
    export_masked "$k" "$v"
  done
)
after=$(grep -c '<<EOF_' "$GITHUB_ENV" 2>/dev/null || true)
echo "fallback=false" >> "$GITHUB_OUTPUT"
echo "OpenBao: exported $((after-before)) non-empty template key(s) to the job; left to the workflow's own constants:${preset:- none}"
