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

umask 077
printf 'TOKEN=%s\nHUB_URL=%s\nKEY="%s"\nLISTEN=%s\n' "$TOKEN" "$HUB_URL" "$KEY" "$PORT" > "$ENV_FILE"
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

# SMART is the entire point of putting an agent on the NAS, so check it rather than
# assume — "no valid SMART data found" leaves a healthy-looking agent and empty panels.
sleep 5
if journalctl -u beszel-agent.service --since "-2 min" --no-pager -o cat 2>/dev/null \
     | grep -q "no valid SMART data found"; then
  printf 'WARNING: agent reports "no valid SMART data found" — disk health is NOT being collected.\n' >&2
  printf '  Check the shim:  sudo -u root env PATH=%s smartctl -j -a /dev/sda | head\n' "$SHIM_DIR" >&2
else
  note "verified: no SMART collection errors in the agent log"
fi

echo "Done. Agent active; check the hub at $HUB_URL"
