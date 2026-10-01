#!/usr/bin/env bash
#
# ci-secrets-from-github-json.sh — FALLBACK for ci-secrets-from-openbao.sh (#609, step 3).
# Used only when OpenBao is unavailable (sealed after a restart, unreachable). Exports EVERY GitHub
# Actions secret the job can see, from `${{ toJSON(secrets) }}` in $ALL_SECRETS, to $GITHUB_ENV.
#
# Because it takes the whole secrets map rather than a list of names, even the fallback needs no
# per-secret passthrough line in the workflow. That list was the fourth hand-kept copy of every
# name; it is gone in both paths. The GitHub-side VALUES can go stale relative to OpenBao, which is
# acceptable for a fallback that only runs while OpenBao is locked.
#
# Same rules as the primary path: every line of every value is masked first (GitHub would mask its
# own secrets anyway; this costs nothing), and a name the workflow already sets is left alone.
set -euo pipefail
: "${ALL_SECRETS:?ALL_SECRETS (toJSON(secrets)) not set}"
: "${GITHUB_ENV:?not running under GitHub Actions}"

n=0
for k in $(printf '%s' "$ALL_SECRETS" | jq -r 'keys[] | select(test("^[A-Za-z_][A-Za-z0-9_]*$"))'); do
  case "$k" in github_token|GITHUB_TOKEN|SUBMODULES_RO_PAT|PATH|HOME|SHELL|USER|IFS|LD_*|GITHUB_*|RUNNER_*|ACTIONS_*) continue;; esac
  [ -n "${!k+x}" ] && continue                      # workflow constants win
  v="$(printf '%s' "$ALL_SECRETS" | jq -r --arg k "$k" '.[$k]')"
  [ -n "$v" ] || continue
  while IFS= read -r line || [ -n "$line" ]; do [ -n "$line" ] && echo "::add-mask::$line"; done <<< "$v"
  d="EOF_$(openssl rand -hex 12)"
  printf '%s<<%s\n%s\n%s\n' "$k" "$d" "$v" "$d" >> "$GITHUB_ENV"
  n=$((n+1))
done
echo "fallback: exported $n GitHub secret(s) to the job"
