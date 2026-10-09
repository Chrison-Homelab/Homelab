#!/usr/bin/env bash
# Capture the API credentials the Homepage dashboard widgets need (ADR-0012) from the apps
# that own them, store each in OpenBao (add-only), and regenerate secrets.env. Run from the repo
# root on a machine with LAN access and the workstation AppRole. Values are never printed.
# (Until #609 finished this wrote Bitwarden SM and mirrored to GitHub secrets; both are retired.)
#
#   BAZARR_API_KEY   Bazarr  config.yaml  auth.apikey            (CT 5103)
#   SEERR_API_KEY    Seerr   settings.json main.apiKey           (CT 5105)
#   PLEX_TOKEN       Plex    Preferences.xml PlexOnlineToken     (CT 5008)  — account-scoped, treat as such
#   ABS_API_KEY      Audiobookshelf — a NEW long-lived API key minted for "homepage-dashboard"
#                    via the admin login in secrets.env (ABS ≥ 2.26 login tokens are short-lived)
#
# Re-runnable: a key already in OpenBao is left alone (rotate with scripts/openbao-set.sh KEY).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
set -a; . ./secrets.env; set +a
. scripts/lib/openbao.sh
NODE="root@hpe-01.homelab.chrison.internal"            # the Media CTs live here
# Overridable: the name is UniFi's auto-registered DHCP hostname, not a declared record, so it
# can briefly answer with a destroyed guest's lease (seen 2026-09-23, right after CT 5014 went).
ABS_URL="${ABS_URL:-http://audiobookshelf.homelab.chrison.internal:13378}"

BAZARR_API_KEY="$(ssh -o BatchMode=yes "$NODE" 'pct exec 5103 -- cat /opt/bazarr/data/config/config.yaml' \
  | python3 -c 'import sys,yaml; print(yaml.safe_load(sys.stdin)["auth"]["apikey"])')"
SEERR_API_KEY="$(ssh -o BatchMode=yes "$NODE" 'pct exec 5105 -- cat /opt/seerr/config/settings.json' | jq -r '.main.apiKey')"
PLEX_TOKEN="$(ssh -o BatchMode=yes "$NODE" 'pct exec 5008 -- cat "/var/lib/plexmediaserver/Library/Application Support/Plex Media Server/Preferences.xml"' \
  | python3 -c 'import sys,re; m=re.search(r"PlexOnlineToken=\"([^\"]+)\"", sys.stdin.read()); print(m.group(1) if m else "")')"

# Minting is NOT idempotent — every call creates another key in Audiobookshelf. So only mint
# when OpenBao has none; secrets.env (sourced above) already carries it if it does.
if [ -z "${ABS_API_KEY:-}" ]; then
ACCESS="$(curl -sf -m 10 -X POST "$ABS_URL/login" -H 'Content-Type: application/json' \
  -d "{\"username\":\"$ABS_USER\",\"password\":\"$ABS_PASSWORD\"}" | jq -r '.user.accessToken // .user.token // empty')"
[ -n "$ACCESS" ] || { echo "ERROR: Audiobookshelf login failed (ABS_USER/ABS_PASSWORD in secrets.env)" >&2; exit 1; }
USER_ID="$(curl -sf -m 10 "$ABS_URL/api/me" -H "Authorization: Bearer $ACCESS" | jq -r .id)"
ABS_API_KEY="$(curl -sf -m 10 -X POST "$ABS_URL/api/api-keys" -H "Authorization: Bearer $ACCESS" -H 'Content-Type: application/json' \
  -d "{\"name\":\"homepage-dashboard\",\"userId\":\"$USER_ID\",\"isActive\":true}" | jq -r '.apiKey.apiKey // .apiKey // empty')"
fi

for k in BAZARR_API_KEY SEERR_API_KEY PLEX_TOKEN ABS_API_KEY; do
  v="${!k}"; [ "${#v}" -ge 16 ] || { echo "ERROR: $k came back empty/short — not stored" >&2; exit 1; }
done

why="$(openbao_unavailable_reason)"; [ -z "$why" ] || { echo "ERROR: OpenBao is $why" >&2; exit 1; }
openbao_login || { echo "ERROR: OpenBao login failed" >&2; exit 1; }
trap '_bao_curl -X POST "$OPENBAO_ADDR/v1/auth/token/revoke-self" >/dev/null 2>&1 || true' EXIT
EXISTING="$(_bao_curl -X LIST "$OPENBAO_ADDR/v1/secret/metadata/$OPENBAO_KV_PREFIX" | jq -r '.data.keys[]?')"
for k in BAZARR_API_KEY SEERR_API_KEY PLEX_TOKEN ABS_API_KEY; do
  if grep -qx "$k" <<<"$EXISTING"; then echo "$k: already in OpenBao — left alone"
  else printf '%s' "${!k}" | openbao_put "$k" && echo "$k: created in OpenBao"; fi
done
scripts/secrets-sync.sh >/dev/null && echo "secrets.env regenerated"
