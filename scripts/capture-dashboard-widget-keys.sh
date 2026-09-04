#!/usr/bin/env bash
# Capture the API credentials the Homepage dashboard widgets need (ADR-0012) from the apps
# that own them, store each in Bitwarden Secrets Manager (add-only), mirror them to the repo's
# GitHub Actions secrets, and regenerate secrets.env. Run from the repo root on a machine with
# LAN access, the bws Keychain token and `gh` auth. Values are never printed.
#
#   BAZARR_API_KEY   Bazarr  config.yaml  auth.apikey            (CT 5103)
#   SEERR_API_KEY    Seerr   settings.json main.apiKey           (CT 5105)
#   PLEX_TOKEN       Plex    Preferences.xml PlexOnlineToken     (CT 5008)  — account-scoped, treat as such
#   ABS_API_KEY      Audiobookshelf — a NEW long-lived API key minted for "homepage-dashboard"
#                    via the admin login in secrets.env (ABS ≥ 2.26 login tokens are short-lived)
#
# Re-runnable: a key already in Secrets Manager is left alone (rotate by deleting it there first).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
set -a; . ./secrets.env; set +a
export BWS_ACCESS_TOKEN="${BWS_ACCESS_TOKEN:-$(security find-generic-password -a bws -s homelab-bws-access-token -w)}"
export BWS_SERVER_URL="${BWS_SERVER_URL:-https://vault.bitwarden.eu}"
PROJECT_ID="ceb88092-7a26-4882-9e7b-b48a000a8f9a"     # SM "Homelab" project (same as secrets-sync.sh)
NODE="root@hpe-01.homelab.chrison.internal"            # the Media CTs live here
ABS_URL="http://audiobookshelf.homelab.chrison.internal:13378"
REPO="Chrison-Homelab/Homelab"

BAZARR_API_KEY="$(ssh -o BatchMode=yes "$NODE" 'pct exec 5103 -- cat /opt/bazarr/data/config/config.yaml' \
  | python3 -c 'import sys,yaml; print(yaml.safe_load(sys.stdin)["auth"]["apikey"])')"
SEERR_API_KEY="$(ssh -o BatchMode=yes "$NODE" 'pct exec 5105 -- cat /opt/seerr/config/settings.json' | jq -r '.main.apiKey')"
PLEX_TOKEN="$(ssh -o BatchMode=yes "$NODE" 'pct exec 5008 -- cat "/var/lib/plexmediaserver/Library/Application Support/Plex Media Server/Preferences.xml"' \
  | python3 -c 'import sys,re; m=re.search(r"PlexOnlineToken=\"([^\"]+)\"", sys.stdin.read()); print(m.group(1) if m else "")')"

ACCESS="$(curl -sf -m 10 -X POST "$ABS_URL/login" -H 'Content-Type: application/json' \
  -d "{\"username\":\"$ABS_USER\",\"password\":\"$ABS_PASSWORD\"}" | jq -r '.user.accessToken // .user.token // empty')"
[ -n "$ACCESS" ] || { echo "ERROR: Audiobookshelf login failed (ABS_USER/ABS_PASSWORD in secrets.env)" >&2; exit 1; }
USER_ID="$(curl -sf -m 10 "$ABS_URL/api/me" -H "Authorization: Bearer $ACCESS" | jq -r .id)"
ABS_API_KEY="$(curl -sf -m 10 -X POST "$ABS_URL/api/api-keys" -H "Authorization: Bearer $ACCESS" -H 'Content-Type: application/json' \
  -d "{\"name\":\"homepage-dashboard\",\"userId\":\"$USER_ID\",\"isActive\":true}" | jq -r '.apiKey.apiKey // .apiKey // empty')"

for k in BAZARR_API_KEY SEERR_API_KEY PLEX_TOKEN ABS_API_KEY; do
  v="${!k}"; [ "${#v}" -ge 16 ] || { echo "ERROR: $k came back empty/short — not stored" >&2; exit 1; }
done

EXISTING="$(bws secret list "$PROJECT_ID" -o json | jq -r '.[].key')"
for k in BAZARR_API_KEY SEERR_API_KEY PLEX_TOKEN ABS_API_KEY; do
  if grep -qx "$k" <<<"$EXISTING"; then echo "$k: already in Secrets Manager — left alone"
  else bws secret create "$k" "${!k}" "$PROJECT_ID" --note "Homepage dashboard widget credential (ADR-0012), captured $(date +%F)" >/dev/null; echo "$k: created in Secrets Manager"; fi
  printf '%s' "${!k}" | gh secret set "$k" --repo "$REPO" && echo "$k: set as Actions secret"
done
scripts/secrets-sync.sh >/dev/null && echo "secrets.env regenerated"
