#!/usr/bin/env bash
#
# openbao-setup-github-oidc.sh — let GitHub Actions log in to OpenBao with its OIDC token (#609, step 3).
# Run once, by Christian, with the Bitwarden CLI unlocked (reads the admin login from it).
#
# Creates (idempotent — re-running re-applies the same config):
#   auth    jwt-github/         trusts https://token.actions.githubusercontent.com (OIDC discovery)
#   policy  github-deploy       READ-ONLY on secret/homelab/* (+ list). CI never writes.
#   role    jwt-github/role/deploy, granted only when ALL of these hold for the job's token:
#             aud                 = openbao-homelab        (what ci-secrets-from-openbao.sh requests)
#             repository          = Chrison-Homelab/Homelab
#             job_workflow_ref    = Chrison-Homelab/Homelab/.github/workflows/_deploy-stack.yml@*
#                                   (the deploy pipeline itself, not any workflow in the repo)
#             runner_environment  = self-hosted
#           and the login comes from 10.0.0.0/8 (the homelab runner). Tokens: 10 min, max 15.
#
#   role    jwt-github/role/discover  (+ policy github-discover): READ on PROXMOX_TOKEN_SECRET only,
#           for discover-drift.yml on refs/heads/main (schedule + dispatch both run there).
#
# WHY job_workflow_ref AND NOT ONLY repository: the repo has other workflows, and a new or edited
# one should not inherit every secret just by asking for an OIDC token. Pinning the reusable
# deploy workflow means only the pipeline that converges stacks reads secrets. It is a glob on the
# ref (@*) because PR previews run from refs/pull/N/merge, which already have the same secrets today.
#
#   export BW_SESSION="$(bw unlock --raw)"
#   scripts/openbao-setup-github-oidc.sh
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/openbao.sh

ITEM_ADMIN="OpenBao CT 3007 — admin login"
MOUNT="jwt-github"; ROLE="deploy"; POLICY="github-deploy"; AUD="openbao-homelab"
REPO="Chrison-Homelab/Homelab"
die() { echo "✗ $*" >&2; exit 1; }
say() { echo "▸ $*"; }

command -v jq >/dev/null || die "jq not found"
[ "$(bw status 2>/dev/null | jq -r .status)" = unlocked ] \
  || die "Bitwarden is locked — run: export BW_SESSION=\"\$(bw unlock --raw)\""
why="$(OPENBAO_TOKEN_PRESET=x openbao_unavailable_reason)"; [ -z "$why" ] || die "OpenBao is $why"

say "logging in as the admin user"
OPENBAO_TOKEN="$(bw get password "$ITEM_ADMIN" | jq -Rsc '{password:.}' \
  | OPENBAO_TOKEN='' _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/userpass/login/christian" \
  | jq -r '.auth.client_token // empty')"
[ -n "$OPENBAO_TOKEN" ] || die "admin login failed"
export OPENBAO_TOKEN
trap '_bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT

say "policy $POLICY (read-only)"
jq -nc --arg p "$OPENBAO_KV_PREFIX" '{policy: (
  "path \"secret/data/\($p)/*\"     { capabilities = [\"read\"] }\n" +
  "path \"secret/metadata/\($p)\"   { capabilities = [\"list\"] }\n" +
  "path \"secret/metadata/\($p)/*\" { capabilities = [\"list\"] }\n")}' \
  | _bao_curl -X PUT --data @- "$OPENBAO_ADDR/v1/sys/policies/acl/$POLICY" >/dev/null

if ! _bao_curl "$OPENBAO_ADDR/v1/sys/auth" | jq -e --arg m "$MOUNT/" '.data[$m] // .[$m]' >/dev/null; then
  say "enabling jwt auth at $MOUNT/"
  printf '{"type":"jwt","description":"GitHub Actions OIDC (#609)"}' \
    | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/sys/auth/$MOUNT" >/dev/null
fi

say "trusting GitHub's OIDC issuer"
printf '%s' '{"oidc_discovery_url":"https://token.actions.githubusercontent.com","bound_issuer":"https://token.actions.githubusercontent.com"}' \
  | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/$MOUNT/config" >/dev/null

say "role $ROLE"
jq -nc --arg aud "$AUD" --arg repo "$REPO" --arg pol "$POLICY" '{
  role_type: "jwt",
  user_claim: "job_workflow_ref",
  bound_audiences: [$aud],
  bound_claims_type: "glob",
  bound_claims: {
    repository: $repo,
    job_workflow_ref: ($repo + "/.github/workflows/_deploy-stack.yml@*"),
    runner_environment: "self-hosted"
  },
  token_policies: [$pol],
  token_ttl: "10m",
  token_max_ttl: "15m",
  token_bound_cidrs: ["10.0.0.0/8"]
}' | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/$MOUNT/role/$ROLE" >/dev/null

say "policy github-discover (PROXMOX_TOKEN_SECRET only)"
jq -nc --arg p "$OPENBAO_KV_PREFIX" '{policy: "path \"secret/data/\($p)/PROXMOX_TOKEN_SECRET\" { capabilities = [\"read\"] }\n"}' \
  | _bao_curl -X PUT --data @- "$OPENBAO_ADDR/v1/sys/policies/acl/github-discover" >/dev/null

say "role discover (discover-drift.yml on main)"
jq -nc --arg aud "$AUD" --arg repo "$REPO" '{
  role_type: "jwt",
  user_claim: "job_workflow_ref",
  bound_audiences: [$aud],
  bound_claims_type: "string",
  bound_claims: {
    repository: $repo,
    job_workflow_ref: ($repo + "/.github/workflows/discover-drift.yml@refs/heads/main"),
    runner_environment: "self-hosted"
  },
  token_policies: ["github-discover"],
  token_ttl: "10m",
  token_max_ttl: "15m",
  token_bound_cidrs: ["10.0.0.0/8"]
}' | _bao_curl -X POST --data @- "$OPENBAO_ADDR/v1/auth/$MOUNT/role/discover" >/dev/null

say "reading back"
for r in "$ROLE" discover; do
  _bao_curl "$OPENBAO_ADDR/v1/auth/$MOUNT/role/$r" \
    | jq -c --arg r "$r" '{role: $r} + (.data | {bound_claims, token_policies, token_ttl, token_bound_cidrs})'
done
say "done — the next deploy run will use OpenBao (watch for 'Secrets from OpenBao' succeeding)"
