#!/usr/bin/env bash
# install-beszel-agent.sh
#
# Installs, updates or removes the Beszel agent on a Synology DSM NAS.
#
# Beszel is being EVALUATED alongside Pulse, not replacing it (#517). Both agents run
# at once on purpose; they collect independently.
#
# ── WHY THIS DOES NOT USE THE UPSTREAM INSTALLER ────────────────────────────────
# The Proxmox side (src/Proxmox/install-beszel-agent.sh) is a thin wrapper around
# https://get.beszel.dev. That installer CANNOT run on DSM, verified on DS1813-01:
#
#   useradd        -> ABSENT      (it does `useradd --system ... beszel`)
#   getent         -> ABSENT      (it does `getent group disk` to grant disk access)
#   /tmp           -> noexec      (so the downloaded script cannot be executed there)
#   $HOME          -> missing     (no /var/services/homes/<user>, so scp fails too)
#
# So this script does the equivalent work directly: fetch the release binary, write our
# own unit, and run as ROOT. Root rather than a dedicated user specifically because DSM
# has no useradd — on Proxmox we keep upstream's unprivileged `beszel` user and grant it
# CAP_SYS_RAWIO instead, which is the better arrangement where it is possible.
#
# ── DISK HEALTH NEEDS A MODERN smartctl, AND BESZEL HAS NO PATH SETTING ─────────
# DSM 7.1.1 ships smartctl 6.5 (2021), which predates `--json`. Beszel shells out to
# `smartctl` and parses JSON, so on stock DSM it collects NOTHING — the same failure
# Pulse hit, for the same reason. src/Synology/build-static-smartctl.sh already puts a
# static 7.5 at /usr/local/bin/smartctl-7 for that.
#
# Pulse could be pointed at it with PULSE_SMARTCTL_PATH. Beszel has NO equivalent —
# its only SMART settings are SMART_DEVICES, EXCLUDE_SMART, SMART_DEVICES_SEPARATOR and
# SMART_INTERVAL (checked against the 0.18.8 binary). It resolves `smartctl` from PATH.
#
# So we put a shim directory FIRST on the unit's PATH containing `smartctl` -> smartctl-7.
# This changes nothing for the rest of DSM: the stock binary at /usr/bin/smartctl is left
# exactly where it is, and only this service sees the shim.
#
# ── SECRETS ─────────────────────────────────────────────────────────────────────
# The enrolment token is never passed in argv (world-readable via /proc) and never left
# in the unit file (mode 644). It goes to a mode-600 EnvironmentFile.
#   1. BESZEL_AGENT_TOKEN in the environment  ← what secrets.env gives you
#   2. --token-file <path>
#   3. --token-stdin
# The hub key comes from BESZEL_HUB_KEY or --key-file.
#
# ── USAGE ───────────────────────────────────────────────────────────────────────
# DSM does not allow root SSH, so run under sudo as a member of `administrators`.
# /tmp is noexec and there is no home directory, so pipe the script in over stdin
# rather than scp-ing it:
#
#   set -a && . ./secrets.env && set +a
#   ssh homelab@nas.homelab.chrison.internal \
#     "cat > /volume1/homes/tmp-install.sh" < src/Synology/install-beszel-agent.sh
#
# Or, on the NAS itself:
#   sudo -E ./install-beszel-agent.sh
#   sudo -E ./install-beszel-agent.sh --uninstall
#   sudo -E ./install-beszel-agent.sh --dry-run
#
# `sudo -E` matters: without it sudo strips BESZEL_AGENT_TOKEN from the environment.

set -euo pipefail

HUB_URL="${BESZEL_HUB_URL:-http://monitoring.homelab.chrison.internal:8090}"
PORT="45876"
VERSION=""                       # empty = latest
SMARTCTL="/usr/local/bin/smartctl-7"
SMART_INTERVAL="15m"
EXCLUDE_MDRAID=true
TOKEN_FILE=""
KEY_FILE=""
TOKEN_STDIN=false
MODE="install"
DRY_RUN=false

BIN=/usr/local/bin/beszel-agent
SHIM_DIR=/usr/local/lib/beszel/bin
ENV_FILE=/etc/beszel-agent.env
UNIT=/etc/systemd/system/beszel-agent.service

die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
note() { printf '  %s\n' "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --token)
      die "--token is refused on purpose: argv is world-readable via /proc.
   Use BESZEL_AGENT_TOKEN in the environment, --token-file, or --token-stdin." ;;
    --token-file)    TOKEN_FILE="${2:?--token-file needs a path}"; shift 2 ;;
    --token-stdin)   TOKEN_STDIN=true; shift ;;
    --key-file)      KEY_FILE="${2:?--key-file needs a path}"; shift 2 ;;
    --hub-url)       HUB_URL="${2:?--hub-url needs a value}"; shift 2 ;;
    --port)          PORT="${2:?--port needs a value}"; shift 2 ;;
    --version)       VERSION="${2:?--version needs a value}"; shift 2 ;;
    --smartctl-path) SMARTCTL="${2:?--smartctl-path needs a path}"; shift 2 ;;
    --keep-mdraid)   EXCLUDE_MDRAID=false; shift ;;
    --smart-interval) SMART_INTERVAL="${2:?--smart-interval needs a value}"; shift 2 ;;
    --uninstall)     MODE="uninstall"; shift ;;
    --dry-run)       DRY_RUN=true; shift ;;
    -h|--help)       sed -n '2,60p' "$0"; exit 0 ;;
    *)               die "unknown argument: $1" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || die "must run as root (use: sudo -E $0)"

if [ "$MODE" = "uninstall" ]; then
  echo "Removing Beszel agent…"
  if [ "$DRY_RUN" = true ]; then
    note "would stop/disable beszel-agent.service and remove $UNIT $BIN $ENV_FILE $SHIM_DIR"
    exit 0
  fi
  systemctl stop beszel-agent.service 2>/dev/null || true
  systemctl disable beszel-agent.service 2>/dev/null || true
  rm -f "$UNIT" "$BIN" "$ENV_FILE" "$SHIM_DIR/smartctl"
  rmdir "$SHIM_DIR" 2>/dev/null || true
  systemctl daemon-reload
  echo "Removed. (smartctl-7 and DSM's own smartctl are left alone.)"
  exit 0
fi

# ── token + key, never via argv ─────────────────────────────────────────────────
TOKEN=""
if [ "$TOKEN_STDIN" = true ]; then
  IFS= read -r TOKEN || true
elif [ -n "$TOKEN_FILE" ]; then
  [ -r "$TOKEN_FILE" ] || die "cannot read --token-file $TOKEN_FILE"
  IFS= read -r TOKEN < "$TOKEN_FILE" || true
else
  TOKEN="${BESZEL_AGENT_TOKEN:-}"
fi
[ -n "$TOKEN" ] || die "no enrolment token. Set BESZEL_AGENT_TOKEN (and remember sudo -E),
   or use --token-file / --token-stdin."

KEY=""
if [ -n "$KEY_FILE" ]; then
  [ -r "$KEY_FILE" ] || die "cannot read --key-file $KEY_FILE"
  IFS= read -r KEY < "$KEY_FILE" || true
else
  KEY="${BESZEL_HUB_KEY:-}"
fi
[ -n "$KEY" ] || die "no hub public key. Set BESZEL_HUB_KEY or use --key-file."
case "$KEY" in ssh-*) : ;; *) die "BESZEL_HUB_KEY does not look like an SSH public key." ;; esac

# ── smartctl shim: the whole reason disk health works here ──────────────────────
[ -x "$SMARTCTL" ] || die "no usable smartctl at $SMARTCTL.
   DSM ships 6.5 which has no --json, so Beszel would collect no disk health at all.
   Build one first:  src/Synology/build-static-smartctl.sh
   (or pass --smartctl-path if it lives elsewhere)"
"$SMARTCTL" -j -V >/dev/null 2>&1 || die "$SMARTCTL does not support --json — wrong binary?"

[ "$(uname -m)" = "x86_64" ] || die "this script assumes x86_64 (got $(uname -m))"

if [ -z "$VERSION" ]; then
  VERSION="$(curl -sfL https://get.beszel.dev/latest-version 2>/dev/null | tr -d '[:space:]')"
  [ -n "$VERSION" ] || die "could not determine the latest agent version"
fi
URL="https://github.com/henrygd/beszel/releases/download/v${VERSION}/beszel-agent_linux_amd64.tar.gz"

echo "Installing Beszel agent v$VERSION on $(hostname)…"
note "hub:      $HUB_URL"
note "smartctl: $SMARTCTL (shimmed onto PATH as 'smartctl')"
if [ "$TOKEN_STDIN" = true ]; then TOKEN_SRC="stdin"
elif [ -n "$TOKEN_FILE" ];  then TOKEN_SRC="$TOKEN_FILE"
else                             TOKEN_SRC="environment"; fi
note "token:    (from $TOKEN_SRC, not argv)"

if [ "$DRY_RUN" = true ]; then
  note "would download $URL"
  note "would install $BIN, shim $SHIM_DIR/smartctl -> $SMARTCTL"
  note "would write $ENV_FILE (mode 600) and $UNIT, then start beszel-agent.service"
  exit 0
fi

# /tmp is noexec on DSM, so stage the download somewhere we can actually use.
STAGE="$(mktemp -d /usr/local/.beszel-stage.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT
curl -sfL "$URL" -o "$STAGE/agent.tar.gz" || die "download failed: $URL"
tar -xzf "$STAGE/agent.tar.gz" -C "$STAGE" || die "could not unpack the agent"
[ -f "$STAGE/beszel-agent" ] || die "archive did not contain beszel-agent"

systemctl stop beszel-agent.service 2>/dev/null || true
install -m 755 -o root -g root "$STAGE/beszel-agent" "$BIN"

mkdir -p "$SHIM_DIR"
ln -sf "$SMARTCTL" "$SHIM_DIR/smartctl"


# ── SMART_INTERVAL ──────────────────────────────────────────────────────────────
# The agent does NOT poll SMART on its own timer. `smartManager.Refresh` is called from
# agent/handlers.go, i.e. only when the HUB asks, and the hub asks on the interval the
# agent advertises. With no SMART_INTERVAL set, disk health was collected once at agent
# start and then sat unchanged for 45+ minutes — which reads as "SMART is broken" when it
# is really "nobody has asked yet". Setting it explicitly makes the cadence ours.

# ── SMART_DEVICES: force -d sat on drives that --scan mislabels as scsi ─────────
# `smartctl --scan` on DSM reports every drive as "-d scsi", and Beszel trusts that
# type and picks its SCSI parser. The SCSI parser finds no ATA attributes, so the hub
# records state=UNKNOWN, temp=0, hours=0 for every disk — and the agent logs NOTHING,
# because from its point of view the scan succeeded. Measured on DS1813-01 /dev/sda:
#   -d scsi -> ata_smart_attributes: False, smart_status: None, temperature: 0
#   -d sat  -> ata_smart_attributes: True,  PASSED,             temperature: 38, 43953h
#
# Beszel never retries with -d sat (the Pulse agent does, which is why Pulse reads these
# disks fine). The supported override is a "path:type" hint in SMART_DEVICES, so probe
# each drive and pin the type we proved works, rather than assuming all of them.
probe_sat_devices() {
  local found="" dev out
  for dev in /dev/sd?; do
    [ -e "$dev" ] || continue
    # NOTE: capture first, then match. `smartctl ... | grep -q` looks correct and is
    # NOT: grep -q exits on the first match, smartctl dies with SIGPIPE (rc 141), and
    # `set -o pipefail` turns that into a failed pipeline — so every device is silently
    # missed. Measured: rc=141 on all four drives.
    out="$("$SMARTCTL" -j -a -d sat "$dev" 2>/dev/null || true)"
    case "$out" in
      *'"ata_smart_attributes"'*) found="${found:+$found,}${dev}:sat" ;;
    esac
  done
  printf '%s' "$found"
}
SMART_DEVICES="$(probe_sat_devices)"
if [ -n "$SMART_DEVICES" ]; then
  note "SMART_DEVICES: $SMART_DEVICES"
else
  printf 'WARNING: no drive answered smartctl -d sat — disk health will read UNKNOWN.\n' >&2
fi


# ── EXCLUDE_SMART: drop the md* arrays, keep the physical disks ─────────────────
# DSM builds md0 (system) and md1 (swap) as RAID1 across every bay the chassis HAS,
# not every bay that is POPULATED. On a DS1813+ with 4 of 8 bays filled that is
# `[8/4] [UUUU____]` — "clean, degraded" with **Failed Devices: 0** and every present
# member `in_sync`. Beszel reports those as FAILED, which is alarming and wrong. It is
# the Synology form of the QNAP bug fixed in henrygd/beszel#2065.
#
# We do not want array health from here anyway — the physical disks are the point, and
# real array state is better read from DSM itself. So exclude every md* device.
#
# Enumerated, not hard-coded: EXCLUDE_SMART is an exact-match set with no wildcard
# support (`filterExcludedDevices` does a map lookup on device.Name), so a hard-coded
# list would silently stop covering a newly created array.
EXCLUDE_SMART=""
if [ "$EXCLUDE_MDRAID" = true ]; then
  for m in /dev/md*; do
    [ -b "$m" ] || continue
    EXCLUDE_SMART="${EXCLUDE_SMART:+$EXCLUDE_SMART,}$m"
  done
  [ -n "$EXCLUDE_SMART" ] && note "excluding mdraid: $EXCLUDE_SMART"
fi

umask 077
{
  printf 'TOKEN=%s\nHUB_URL=%s\nKEY="%s"\nLISTEN=%s\n' "$TOKEN" "$HUB_URL" "$KEY" "$PORT"
  [ -n "$SMART_DEVICES" ] && printf 'SMART_DEVICES=%s\n' "$SMART_DEVICES"
  [ -n "$SMART_INTERVAL" ] && printf 'SMART_INTERVAL=%s\n' "$SMART_INTERVAL"
  [ -n "$EXCLUDE_SMART" ] && printf 'EXCLUDE_SMART=%s\n' "$EXCLUDE_SMART"
} > "$ENV_FILE"
chmod 600 "$ENV_FILE"; chown root:root "$ENV_FILE"

# Our own unit — upstream's installer cannot run here (see the header). Deliberately
# plain: DSM's kernel is 3.10 and most of systemd's sandboxing options upstream sets
# are either unsupported or silently ignored on it, so claiming them would be theatre.
cat > "$UNIT" <<EOF
[Unit]
Description=Beszel Agent
Wants=network-online.target
After=network-online.target

[Service]
# The shim MUST come first: Beszel resolves 'smartctl' from PATH and has no setting to
# override it, and DSM's own /usr/bin/smartctl 6.5 has no --json.
Environment="PATH=$SHIM_DIR:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
EnvironmentFile=$ENV_FILE
ExecStart=$BIN
User=root
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
chmod 644 "$UNIT"

systemctl daemon-reload
systemctl enable beszel-agent.service >/dev/null 2>&1 || true
systemctl start beszel-agent.service

sleep 3
systemctl is-active --quiet beszel-agent.service || {
  journalctl -u beszel-agent.service -n 20 --no-pager 2>/dev/null >&2 || true
  die "beszel-agent did not stay up"
}
note "verified: service active"

# The unit is mode 644; make sure we did not leak the token into it.
if grep -q "$TOKEN" "$UNIT" 2>/dev/null; then
  die "token leaked into $UNIT (mode 644) — this should be impossible, investigate"
fi
note "verified: token is only in $ENV_FILE (mode 600), not the unit"

# SMART is the entire point of putting an agent on the NAS, so check it.
#
# NOTE ON WHAT THIS CAN AND CANNOT PROVE. An empty agent log is NOT evidence of success:
# when --scan mislabels the drives as scsi, the agent parses nothing, logs nothing, and
# the hub silently shows UNKNOWN/0°C/0h. That false reassurance is exactly how this was
# missed the first time round. So assert on the DATA, not on the absence of errors.
sleep 5
AGENTLOG="$(journalctl -u beszel-agent.service --since "-2 min" --no-pager -o cat 2>/dev/null || true)"
case "$AGENTLOG" in *"no valid SMART data found"*) SMART_ERR=true ;; *) SMART_ERR=false ;; esac
if [ "$SMART_ERR" = true ]; then
  printf 'WARNING: agent reports "no valid SMART data found" — disk health is NOT collected.\n' >&2
elif [ -z "$SMART_DEVICES" ]; then
  printf 'WARNING: no SATA devices were pinned, so the hub will show UNKNOWN for every disk.\n' >&2
else
  # Positive check: the exact command the agent runs for the first pinned device.
  FIRST_DEV="${SMART_DEVICES%%:*}"
  PROBE="$("$SMARTCTL" -j -a -d sat "$FIRST_DEV" 2>/dev/null || true)"
  case "$PROBE" in *'"ata_smart_attributes"'*) OK=true ;; *) OK=false ;; esac
  if [ "$OK" = true ]; then
    note "verified: $FIRST_DEV returns ATA attributes under the pinned -d sat type"
    note "  confirm end-to-end in the hub — a disk showing UNKNOWN/0°C means it did not land"
  else
    printf 'WARNING: %s returned no ATA attributes under -d sat.\n' "$FIRST_DEV" >&2
  fi
fi

echo "Done. Agent active; check the hub at $HUB_URL"
