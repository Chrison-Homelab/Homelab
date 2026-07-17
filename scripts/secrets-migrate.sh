#!/usr/bin/env bash
#
# secrets-migrate.sh — one-shot: seed Bitwarden Secrets Manager (SM) from the
# existing password vault + the local secrets.env. Idempotent + safe to re-run.
#
#   • VAULT IS READ-ONLY here — we only `bw get`, never create/edit/delete items.
#   • SM is ADD-ONLY — existing SM secrets are left untouched (skipped, not
#     overwritten). Nothing is ever deleted.
#   • No secret value is ever printed; only a created/skipped summary.
#
# Prereqs:
#   • bws installed + `bws config server-base https://vault.bitwarden.eu` (EU).
#   • Access token in macOS Keychain (service homelab-bws-access-token / acct bws).
#   • bw CLI logged in. Unlock: either export BW_SESSION, or run interactively and
#     this script will `bw unlock` for you.
#
# Usage:  scripts/secrets-migrate.sh            # from repo root (needs secrets.env)
#
set -euo pipefail

PROJECT_ID="ceb88092-7a26-4882-9e7b-b48a000a8f9a"   # SM "Homelab" project
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRETS_ENV="$REPO_ROOT/secrets.env"

# ── bws access token from Keychain + EU region ──
export BWS_ACCESS_TOKEN="$(security find-generic-password -a bws -s homelab-bws-access-token -w)"
export BWS_SERVER_URL="${BWS_SERVER_URL:-https://vault.bitwarden.eu}"

# ── ensure the bw vault is unlocked ──
if [ -z "${BW_SESSION:-}" ]; then
  if [ "$(bw status | jq -r '.status')" != "unlocked" ]; then
    echo "Unlocking bw vault (master password)…" >&2
    BW_SESSION="$(bw unlock --raw)"
  fi
fi
export BW_SESSION
[ -n "${BW_SESSION:-}" ] || { echo "ERROR: bw vault not unlocked (set BW_SESSION)"; exit 1; }

# ── secrets.env (source of the 6 local-only keys) ──
[ -f "$SECRETS_ENV" ] || { echo "ERROR: $SECRETS_ENV not found"; exit 1; }

# Mapping table: ENV_KEY | source | arg
#   pw     → bw get password <itemid>
#   field  → bw get item <itemid>, read custom field <fieldname>   (arg = itemid:field)
#   local  → value already exported from secrets.env               (arg = env var name)
MAP=$(cat <<'EOF'
PROXMOX_TOKEN_SECRET|pw|31b634e6-9c90-483a-923f-b4870010a2fe
SYNOLOGY_PASSWORD|pw|fd106006-1d6f-437f-bc35-b363008810f8
UNIFI_API_KEY|pw|01b67ed6-ba7f-46df-b565-b45b008ca969
CF_API_TOKEN|pw|627e8845-1445-405d-8e0c-b4690026f80d
GITHUB_PACKAGES_PAT|pw|6a2386d0-e4f9-4fc7-92df-b47600124de5
SONARR_PASSWORD|pw|4bbcbe5f-f11d-4f69-9bc3-b47100c93fd3
RADARR_PASSWORD|pw|cc1ebc6e-ffe3-4f9c-aca5-b47100c951b1
PROWLARR_PASSWORD|pw|5c624637-8196-44f5-9c7c-b47100c95992
BAZARR_PASSWORD|pw|7b7877b4-45e5-4cfa-aa43-b47100c960c7
QBIT_PASSWORD|pw|a2ce1bc5-6ac8-4ddb-813a-b4700028aa7d
ABS_PASSWORD|pw|a2ad6629-2778-4129-ab0e-b47100a4d8e1
BAZARR_OPENSUBTITLES_PASSWORD|pw|5f34d54b-465b-4748-922f-b41a016e68cc
PANGOLIN_API_KEY|pw|f6b23a86-335c-4ce7-9a07-b487001214a0
HARDCOVER_API_KEY|field|526c120c-b9a4-4295-be14-b40900a887f6:API Key
HOMEASSISTANT_TOKEN|field|74062fd8-a8e7-4976-82fd-b3670078b841:Token
CF_ACCOUNT_ID|local|CF_ACCOUNT_ID
CF_ACCESS_PROXMOX_CLIENT_ID|local|CF_ACCESS_PROXMOX_CLIENT_ID
CF_ACCESS_PROXMOX_CLIENT_SECRET|local|CF_ACCESS_PROXMOX_CLIENT_SECRET
CF_TUNNEL_MEDIA_TOKEN|local|CF_TUNNEL_MEDIA_TOKEN
PANGOLIN_LICENSE_KEY|local|PANGOLIN_LICENSE_KEY
GH_RUNNER_PAT|local|GH_RUNNER_PAT
EOF
)

# Pull the 6 local values from secrets.env (in a subshell-free source; values stay
# in-process, never echoed).
set -a; . "$SECRETS_ENV"; set +a

# Existing SM keys (add-only: skip anything already present).
echo "Reading existing SM secrets…" >&2
EXISTING="$(bws secret list "$PROJECT_ID" -o json | jq -r '.[].key')"

created=0; skipped=0; missing=0
while IFS='|' read -r KEY SRC ARG; do
  [ -n "$KEY" ] || continue

  if printf '%s\n' "$EXISTING" | grep -qxF "$KEY"; then
    echo "  = skip   $KEY (already in SM)"; skipped=$((skipped+1)); continue
  fi

  case "$SRC" in
    pw)    VAL="$(bw get password "$ARG" --session "$BW_SESSION")" ;;
    field) ID="${ARG%%:*}"; FN="${ARG#*:}"
           VAL="$(bw get item "$ID" --session "$BW_SESSION" | jq -r --arg n "$FN" '.fields[]|select(.name==$n)|.value')" ;;
    local) VAL="$(printf '%s' "${!ARG:-}")" ;;
    *)     echo "  ! bad source '$SRC' for $KEY"; continue ;;
  esac

  if [ -z "$VAL" ] || [ "$VAL" = "null" ]; then
    echo "  ! MISSING $KEY (empty at source '$SRC:$ARG') — NOT created"
    missing=$((missing+1)); continue
  fi

  bws secret create "$KEY" "$VAL" "$PROJECT_ID" -o none >/dev/null
  VAL=""   # drop from memory asap
  echo "  + create $KEY"; created=$((created+1))
done <<< "$MAP"

echo
echo "Done. created=$created  skipped=$skipped  missing=$missing"
echo "Verify: BWS_ACCESS_TOKEN=… bws secret list $PROJECT_ID -o table"
