#!/usr/bin/env bash
# install-beszel-agent.sh
#
# Installs, updates or removes the Beszel agent on a Proxmox VE node.
#
# Beszel is being EVALUATED alongside Pulse, not replacing it (#517). Running both
# agents at once is deliberate and fine — they collect independently and neither takes
# an exclusive lock on anything.
#
# Thin wrapper around the upstream installer at https://get.beszel.dev, so the agent
# is never vendored here and cannot drift from the project.
#
# ── SECRETS ─────────────────────────────────────────────────────────────────────
# The enrolment token is NEVER passed on the command line, and NEVER left in the
# systemd unit. Two separate exposures, both real:
#
#   1. argv is world-readable via /proc — upstream's documented `-t <token>` leaks the
#      token to every local user for the duration of the install.
#   2. upstream writes Environment="TOKEN=..." straight into
#      /etc/systemd/system/beszel-agent.service, and systemd units are mode 644.
#      That is a permanent world-readable copy, which is worse than the transient one.
#
# So we run the upstream installer WITHOUT -t (the -k public key is fine in argv, it is
# public material), then write the token to a mode-600 root-owned EnvironmentFile and
# point the unit at it with a DROP-IN.
#
# A drop-in rather than editing the unit, for the same reason as the NAS smartctl fix:
# the upstream installer rewrites beszel-agent.service on every run, including its own
# auto-update path. An inline edit would silently revert; a drop-in survives.
#
# Supply the token one of these ways (checked in order):
#   1. BESZEL_AGENT_TOKEN in the environment  ← what secrets.env gives you
#   2. --token-file <path>                    ← a mode-600 file
#   3. --token-stdin                          ← piped in
# The hub key comes from BESZEL_HUB_KEY or --key-file.
#
# From a checkout:
#   set -a && . ./secrets.env && set +a
#
# ── USAGE ───────────────────────────────────────────────────────────────────────
#   ./install-beszel-agent.sh                     # install (or update if present)
#   ./install-beszel-agent.sh --uninstall         # remove agent + drop-in + env file
#   ./install-beszel-agent.sh --dry-run           # print what would happen
#   ./install-beszel-agent.sh --hub-url http://monitoring.homelab.chrison.internal:8090
#   ./install-beszel-agent.sh --port 45876
#
# ── PRIVILEGE ───────────────────────────────────────────────────────────────────
# Upstream runs the agent as an unprivileged `beszel` user and adds it to the `disk`
# group, which is what lets it read S.M.A.R.T. without root. That is a better default
# than the Pulse agent's root profile, and we keep it. If SMART comes back empty on a
# node, check group membership before reaching for root.
#
# Requirements: root on a PVE node, curl.

set -euo pipefail

HUB_URL="${BESZEL_HUB_URL:-http://monitoring.homelab.chrison.internal:8090}"
PORT="45876"
TOKEN_FILE=""
KEY_FILE=""
TOKEN_STDIN=false
SYSTEM_NAME=""
SMART_INTERVAL="15m"
MODE="install"
DRY_RUN=false

ENV_FILE=/etc/beszel-agent.env
DROPIN_DIR=/etc/systemd/system/beszel-agent.service.d
DROPIN="$DROPIN_DIR/10-homelab.conf"

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
note() { printf '  %s\n' "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --token)
      die "--token is refused on purpose: argv is world-readable via /proc.
   Use BESZEL_AGENT_TOKEN in the environment, --token-file, or --token-stdin." ;;
    --token-file)  TOKEN_FILE="${2:?--token-file needs a path}"; shift 2 ;;
    --token-stdin) TOKEN_STDIN=true; shift ;;
    --key-file)    KEY_FILE="${2:?--key-file needs a path}"; shift 2 ;;
    --hub-url)     HUB_URL="${2:?--hub-url needs a value}"; shift 2 ;;
    --port)        PORT="${2:?--port needs a value}"; shift 2 ;;
    --system-name) SYSTEM_NAME="${2:?--system-name needs a value}"; shift 2 ;;
    --smart-interval) SMART_INTERVAL="${2:?--smart-interval needs a value}"; shift 2 ;;
    --uninstall)   MODE="uninstall"; shift ;;
    --dry-run)     DRY_RUN=true; shift ;;
    -h|--help)     sed -n '2,52p' "$0"; exit 0 ;;
    *)             die "unknown argument: $1" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || die "must run as root"

# ── uninstall ───────────────────────────────────────────────────────────────────
if [ "$MODE" = "uninstall" ]; then
  echo "Removing Beszel agent…"
  if [ "$DRY_RUN" = true ]; then
    note "would run: /tmp/beszel-install.sh -u"
    note "would remove: $DROPIN and $ENV_FILE"
    exit 0
  fi
  curl -sL https://get.beszel.dev -o /tmp/beszel-install.sh && chmod +x /tmp/beszel-install.sh
  /tmp/beszel-install.sh -u || true
  rm -f "$DROPIN" "$ENV_FILE"
  rmdir "$DROPIN_DIR" 2>/dev/null || true
  systemctl daemon-reload
  rm -f /tmp/beszel-install.sh
  echo "Removed."
  exit 0
fi

# ── resolve token + key without ever putting them in argv ───────────────────────
TOKEN=""
if [ "$TOKEN_STDIN" = true ]; then
  IFS= read -r TOKEN || true
elif [ -n "$TOKEN_FILE" ]; then
  [ -r "$TOKEN_FILE" ] || die "cannot read --token-file $TOKEN_FILE"
  IFS= read -r TOKEN < "$TOKEN_FILE" || true
else
  TOKEN="${BESZEL_AGENT_TOKEN:-}"
fi
[ -n "$TOKEN" ] || die "no enrolment token. Set BESZEL_AGENT_TOKEN, or use --token-file / --token-stdin.
   From a checkout:  set -a && . ./secrets.env && set +a"

KEY=""
if [ -n "$KEY_FILE" ]; then
  [ -r "$KEY_FILE" ] || die "cannot read --key-file $KEY_FILE"
  IFS= read -r KEY < "$KEY_FILE" || true
else
  KEY="${BESZEL_HUB_KEY:-}"
fi
[ -n "$KEY" ] || die "no hub public key. Set BESZEL_HUB_KEY or use --key-file."

case "$KEY" in
  ssh-*) : ;;
  *) die "BESZEL_HUB_KEY does not look like an SSH public key (expected it to start with 'ssh-')." ;;
esac

echo "Installing Beszel agent on $(hostname -s)…"
note "hub:  $HUB_URL"
note "port: $PORT"
if [ "$TOKEN_STDIN" = true ]; then TOKEN_SRC="stdin"
elif [ -n "$TOKEN_FILE" ];  then TOKEN_SRC="$TOKEN_FILE"
else                             TOKEN_SRC="environment"; fi
note "token: (from $TOKEN_SRC, not argv)"
[ -n "$SYSTEM_NAME" ] && note "name:  $SYSTEM_NAME (SYSTEM_NAME; the LXCs are all literally hostname 'podman-host')"


# ── SMART_INTERVAL ──────────────────────────────────────────────────────────────
# The agent does NOT poll SMART on its own timer. `smartManager.Refresh` is called from
# agent/handlers.go, i.e. only when the HUB asks, and the hub asks on the interval the
# agent advertises. With no SMART_INTERVAL set, disk health was collected once at agent
# start and then sat unchanged for 45+ minutes — which reads as "SMART is broken" when it
# is really "nobody has asked yet". Setting it explicitly makes the cadence ours.

# ── two host shapes, detected rather than declared ──────────────────────────────
# A PVE NODE has physical disks and no podman: the agent stays as upstream's
# unprivileged `beszel` user and gets CAP_SYS_RAWIO so it can read SMART.
#
# A PODMAN HOST (the quadlet LXCs — Monitoring 4001, Media 5114, SmartHome 6004,
# DevOps 3006) has no physical disks at all, so CAP_SYS_RAWIO would buy nothing. What it
# does have is a ROOTLESS podman socket, which is what Beszel reads container stats from.
#
# That socket cannot be reached by adding `beszel` to a group: /run/user/1000 is mode
# 0700, so the parent directory blocks traversal no matter what the socket itself allows.
# Only root or the owning user can get in. Running the agent AS `podman` is the
# lower-privilege of the two, and it is the user that already owns every container here.
PODMAN_UID="$(id -u podman 2>/dev/null || true)"
PODMAN_SOCK="/run/user/${PODMAN_UID:-0}/podman/podman.sock"
if [ -n "$PODMAN_UID" ] && [ -S "$PODMAN_SOCK" ]; then
  IS_PODMAN_HOST=true
  note "podman host: reading containers from $PODMAN_SOCK as user 'podman'"
else
  IS_PODMAN_HOST=false
fi

if [ "$DRY_RUN" = true ]; then
  note "would run: /tmp/beszel-install.sh -k '<hub key>' -url '$HUB_URL' -p '$PORT'   (no -t)"
  note "would write $ENV_FILE (mode 600) with TOKEN + HUB_URL"
  note "would write $DROPIN pointing EnvironmentFile at it"
  exit 0
fi

# Upstream installer. -k and -url are public; -t is deliberately omitted so the token
# never reaches argv. The unit it writes will carry an EMPTY TOKEN=, which the drop-in
# below overrides.
curl -sL https://get.beszel.dev -o /tmp/beszel-install.sh || die "could not download the upstream installer"
chmod +x /tmp/beszel-install.sh
/tmp/beszel-install.sh -k "$KEY" -url "$HUB_URL" -p "$PORT" </dev/null
rm -f /tmp/beszel-install.sh

# ── token into a mode-600 EnvironmentFile, referenced from a drop-in ────────────
umask 077
{
  printf 'TOKEN=%s\nHUB_URL=%s\n' "$TOKEN" "$HUB_URL"
  # All four podman hosts share the hostname `podman-host` — generic by deliberate
  # convention in the stack yamls — so without this they are indistinguishable in the
  # hub. Their machine-ids DO differ, so fingerprints do not collide; this is purely
  # about the display name. Read by the agent as SYSTEM_NAME (agent/client.go).
  [ -n "$SYSTEM_NAME" ] && printf 'SYSTEM_NAME=%s\n' "$SYSTEM_NAME"
  [ -n "$SMART_INTERVAL" ] && printf 'SMART_INTERVAL=%s\n' "$SMART_INTERVAL"
} > "$ENV_FILE"
chmod 600 "$ENV_FILE"
chown root:root "$ENV_FILE"

mkdir -p "$DROPIN_DIR"
cat > "$DROPIN" <<EOF
# Managed by the Homelab IaC repo: src/Proxmox/install-beszel-agent.sh
#
# The upstream unit hard-codes Environment="TOKEN=..." and systemd units are mode 644,
# i.e. the enrolment token would be readable by every local user. This drop-in supplies
# it from a mode-600 root-owned file instead.
#
# A drop-in, not an edit: the upstream installer rewrites beszel-agent.service on every
# run and on auto-update, so an inline change would silently revert.
[Service]
EnvironmentFile=$ENV_FILE

# CAP_SYS_RAWIO — without it the agent collects NO SMART data at all on a PVE node.
#
# Upstream adds the beszel user to the \`disk\` group, which grants read/write on the
# block device but NOT the ATA passthrough (SG_IO) needed to talk to the drive. smartctl
# then cannot even auto-detect the transport and gives up with "Probable ATA device
# behind a SAT layer", exit 0 and no attributes — so the agent logs the misleading
# "no valid SMART data found" and everything downstream looks merely empty, not broken.
#
# Measured on hpe-01, not assumed:
#   runuser -u beszel  smartctl -a /dev/sda        -> "Probable ATA device behind a SAT layer"
#   ... same, forcing -d sat                       -> "Read Device Identity failed: Operation not permitted"
#   ... same, with CAP_SYS_RAWIO added             -> full attributes, health PASSED
# So -d sat is NOT the fix here; the missing privilege is.
#
# This is a real grant, but a small delta: the disk group already confers read/write on
# the raw block devices. Running the agent as root instead would be strictly worse.
EOF

if [ "$IS_PODMAN_HOST" = true ]; then
  cat >> "$DROPIN" <<EOF
# Podman host: no physical disks, so no CAP_SYS_RAWIO. Run as the socket's owner and
# point Beszel's Docker client at podman's Docker-compatible API.
User=podman
Group=podman
Environment=DOCKER_HOST=unix://$PODMAN_SOCK
EOF
else
  cat >> "$DROPIN" <<EOF
AmbientCapabilities=CAP_SYS_RAWIO
CapabilityBoundingSet=CAP_SYS_RAWIO
EOF
fi
chmod 644 "$DROPIN"

systemctl daemon-reload
systemctl restart beszel-agent.service

# ── verify the drop-in actually WON, rather than assuming systemd's merge order ──
# The unit sets an empty TOKEN= before the drop-in's EnvironmentFile is read. Later
# assignments override earlier ones, so the file should win — but that is exactly the
# kind of "looks right, is not" assumption worth checking on the box.
sleep 2
if ! systemctl is-active --quiet beszel-agent.service; then
  systemctl status beszel-agent.service --no-pager -l | tail -20 >&2
  die "beszel-agent did not come up"
fi

RESOLVED=$(systemctl show beszel-agent.service -p Environment --value 2>/dev/null || true)
case "$RESOLVED" in
  *TOKEN=?*) note "verified: TOKEN resolved from $ENV_FILE (drop-in wins)" ;;
  *) die "TOKEN did not resolve — the drop-in lost to the unit's own empty Environment=.
   Inspect: systemctl show beszel-agent.service -p Environment" ;;
esac

if [ -f /etc/systemd/system/beszel-agent.service ] \
   && grep -qE '^Environment="TOKEN=.+"' /etc/systemd/system/beszel-agent.service; then
  printf 'WARNING: upstream unit still contains a non-empty TOKEN= — %s\n' \
         "that file is mode 644 and readable by any local user." >&2
fi

# ── verify SMART is actually being collected ────────────────────────────────────
# "no valid SMART data found" is what a missing CAP_SYS_RAWIO looks like, and it is easy
# to miss because the agent stays healthy and the disk simply shows no health at all.
# Checked rather than trusted, because an empty panel and a working one look identical
# until you go looking.
# Capture then match: `journalctl | grep -q` under `set -o pipefail` reports the
# pipeline as FAILED when grep exits early and journalctl takes SIGPIPE (rc 141), so
# the warning below could never fire and this check would always claim success.
AGENTLOG="$(journalctl -u beszel-agent.service --since "-2 min" --no-pager -o cat 2>/dev/null || true)"
case "$AGENTLOG" in *"no valid SMART data found"*) SMART_ERR=true ;; *) SMART_ERR=false ;; esac

if [ "$IS_PODMAN_HOST" = true ]; then
  # Containers are the point here, not disks — assert on what the agent will actually read.
  NCONT="$(curl -s --max-time 5 --unix-socket "$PODMAN_SOCK" http://d/v1.41/containers/json 2>/dev/null \
            | grep -o '"Id"' | wc -l | tr -d ' ' || echo 0)"
  if [ "${NCONT:-0}" -gt 0 ]; then
    note "verified: podman socket reachable, $NCONT running container(s) visible"
  else
    printf 'WARNING: the podman socket returned no containers — Beszel will show none.\n' >&2
    printf '  Check: curl --unix-socket %s http://d/v1.41/containers/json\n' "$PODMAN_SOCK" >&2
  fi
  echo "Done. Agent active; check the hub at $HUB_URL"
  exit 0
fi

if [ "$SMART_ERR" = true ]; then
  printf 'WARNING: agent reports "no valid SMART data found" — SMART is NOT being collected.\n' >&2
  printf '  Check that the drop-in granted CAP_SYS_RAWIO:\n' >&2
  printf '    systemctl show beszel-agent.service -p AmbientCapabilities\n' >&2
else
  note "verified: no SMART collection errors in the agent log"
fi

echo "Done. Agent active; check the hub at $HUB_URL"
