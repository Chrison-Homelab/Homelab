#!/usr/bin/env bash
# install-pulse-agent.sh
#
# Installs, updates or removes the Pulse unified agent on a Synology DSM NAS.
#
# Unlike the Proxmox nodes, the NAS has NO API integration in Pulse at all — without
# an agent it is simply absent from the dashboard. This is the only way to get eyes
# on it: disk S.M.A.R.T. health, volume/filesystem capacity, temperatures, CPU and
# memory, plus every container if Container Manager is installed.
#
# Thin wrapper around the installer served by the Pulse SERVER at $PULSE_URL/install.sh,
# which detects DSM natively (it keys off /usr/syno + /etc/VERSION) and installs a
# systemd unit on DSM 7+, or an Upstart job on DSM 6.x.
#
# ── SECRETS ─────────────────────────────────────────────────────────────────────
# The API token is NEVER passed on the command line. Supply it one of these ways:
#
#   1. PULSE_API_TOKEN in the environment   ← what secrets.env gives you
#   2. --token-file <path>                  ← a mode-600 file
#   3. --token-stdin                        ← piped in
#
# ── USAGE ───────────────────────────────────────────────────────────────────────
# DSM does not let you SSH in as root, so run this under sudo as a user in the
# `administrators` group. Driving it from a workstation checkout:
#
#   set -a && . ./secrets.env && set +a
#   scp src/Synology/install-pulse-agent.sh homelab@nas.homelab.chrison.internal:/tmp/
#   ssh -t homelab@nas.homelab.chrison.internal \
#       "PULSE_API_TOKEN='...' sudo -E bash /tmp/install-pulse-agent.sh"
#
# Or, on the NAS itself:
#   sudo -E ./install-pulse-agent.sh              # install (or update if present)
#   sudo -E ./install-pulse-agent.sh --uninstall  # remove agent + deregister
#   sudo -E ./install-pulse-agent.sh --dry-run    # print the command, change nothing
#
# `sudo -E` matters: without it sudo strips PULSE_API_TOKEN from the environment.
#
# ── DISK HEALTH DOES NOT WORK ON DSM 7.1 (known limitation) ─────────────────────
# The agent enumerates the disks correctly — model, size, device path all show up in
# Pulse — but health reads UNKNOWN and temperature 0 on every one of them.
#
# Cause: the agent collects S.M.A.R.T. by running
#     smartctl -n standby,3 -i -A -H --json=o /dev/sdX
# (verified by logging the agent's own invocations). DSM 7.1.1 ships smartctl **6.5**,
# which predates JSON output entirely — smartmontools only added it in 7.0. 6.5 ignores
# --json, prints its banner, and exits, so the agent parses nothing.
#
# NOT a device-type problem. The agent already retries each disk with `-d sat` by itself,
# so wrapping smartctl to force sat changes nothing. Verified: with `-d sat` the DSM
# binary returns a correct ATA report, but still not as JSON.
#
# The fix is a smartctl >= 7.0 on the NAS, pointed at via the PULSE_SMARTCTL_PATH
# environment variable that the agent honours (a systemd drop-in on DSM 7+ survives the
# installer rewriting its unit). That means putting a third-party binary on the NAS, so it
# is deliberately NOT done here.
#
# Until then, disk health on the NAS comes from DSM's own Storage Manager and its
# notification e-mails; Pulse still gives you volume capacity, CPU, memory and network.
#
# ── THE md0 / md1 FALSE POSITIVE ────────────────────────────────────────────────
# DSM keeps its system partition on /dev/md0 and swap on /dev/md1, both mirrored
# across every disk in the box. DSM deliberately suppresses their non-critical
# states; Pulse treats them as ordinary RAID devices and raises PERMANENT critical
# "unhealthy" alerts while DSM's own Storage Manager reports everything fine
# (upstream issue #970 — closed as an enhancement request, never fixed).
#
# Two false criticals that can never be cleared will train you to ignore the alert
# panel, so this script excludes them by default. Your DATA volumes (md2, md3, ...)
# are untouched and still monitored. Pass --no-disk-exclude-defaults to see the
# raw upstream behaviour.
#
# ── PRIVILEGE ───────────────────────────────────────────────────────────────────
# The agent runs as root, and that is not a choice we get to make: the upstream
# installer explicitly REFUSES --least-privilege on appliance platforms (Synology,
# QNAP, TrueNAS, Unraid) rather than silently falling back, because their service
# managers and vendor tooling assume root.
#
# Requirements: DSM 6.x or 7+, root (via sudo), curl, reachability to the Pulse server.

set -euo pipefail

PULSE_URL="${PULSE_URL:-http://monitoring.homelab.chrison.internal:7655}"
INTERVAL=""
ENABLE_COMMANDS=true
DISK_EXCLUDE_DEFAULTS=true
TOKEN_FILE=""
TOKEN_STDIN=false
MODE="install"
DRY_RUN=false
FORCE=false

usage() { sed -n '2,60p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

log()  { printf '\033[0;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[0;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[0;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --url)          PULSE_URL="$2"; shift 2 ;;
        --interval)     INTERVAL="$2"; shift 2 ;;
        --token-file)   TOKEN_FILE="$2"; shift 2 ;;
        --token-stdin)  TOKEN_STDIN=true; shift ;;
        --no-commands)  ENABLE_COMMANDS=false; shift ;;
        --no-disk-exclude-defaults) DISK_EXCLUDE_DEFAULTS=false; shift ;;
        --update)       MODE="update"; shift ;;
        --uninstall)    MODE="uninstall"; shift ;;
        --dry-run)      DRY_RUN=true; shift ;;
        --force)        FORCE=true; shift ;;
        -h|--help)      usage 0 ;;
        --token)
            die "--token is refused on purpose: argv is world-readable via /proc.
       Use PULSE_API_TOKEN in the environment, --token-file, or --token-stdin." ;;
        *) echo "Unknown option: $1" >&2; usage 1 ;;
    esac
done

[ "$(id -u)" -eq 0 ] || die "Must run as root. DSM blocks direct root SSH, so use:
       sudo -E $0 $*
       (-E preserves PULSE_API_TOKEN; plain sudo strips it.)"

if [ "$FORCE" = false ] && { [ ! -d /usr/syno ] || [ ! -f /etc/VERSION ]; }; then
    die "This does not look like a Synology DSM system (no /usr/syno + /etc/VERSION). Use --force to override."
fi

command -v curl >/dev/null 2>&1 || die "curl is required."

DSM_MAJOR="$(grep 'majorversion=' /etc/VERSION 2>/dev/null | cut -d'"' -f2 || echo '?')"

# ── Resolve the token into a mode-600 file ──────────────────────────────────────
OWN_TOKEN_FILE=""
cleanup() { [ -n "$OWN_TOKEN_FILE" ] && rm -f "$OWN_TOKEN_FILE"; }
trap cleanup EXIT

resolve_token() {
    if [ -n "$TOKEN_FILE" ]; then
        [ -r "$TOKEN_FILE" ] || die "Token file not readable: $TOKEN_FILE"
        return
    fi
    local token=""
    if [ "$TOKEN_STDIN" = true ]; then
        IFS= read -r token || true
    elif [ -n "${PULSE_API_TOKEN:-}" ]; then
        token="$PULSE_API_TOKEN"
    else
        die "No API token. Set PULSE_API_TOKEN and use 'sudo -E', or pass --token-file / --token-stdin."
    fi
    [ -n "$token" ] || die "Empty API token."
    OWN_TOKEN_FILE="$(mktemp)"
    chmod 600 "$OWN_TOKEN_FILE"
    printf '%s' "$token" > "$OWN_TOKEN_FILE"
    TOKEN_FILE="$OWN_TOKEN_FILE"
}

[ "$MODE" = "uninstall" ] || resolve_token

# ── Build the upstream installer arguments ──────────────────────────────────────
build_args() {
    ARGS=(--url "$PULSE_URL")
    case "$MODE" in
        uninstall) ARGS+=(--uninstall); return ;;
        update)    ARGS+=(--update --token-file "$TOKEN_FILE") ;;
        install)   ARGS+=(--token-file "$TOKEN_FILE") ;;
    esac
    # No --least-privilege: upstream refuses it on DSM (see PRIVILEGE above).
    # No --enable-proxmox / --enable-kubernetes: auto-detect correctly finds neither.
    # Container Manager, if installed, is auto-detected as a Docker host.
    [ "$ENABLE_COMMANDS" = true ] && ARGS+=(--enable-commands)
    if [ "$DISK_EXCLUDE_DEFAULTS" = true ]; then
        ARGS+=(--disk-exclude md0 --disk-exclude md1)
    fi
    [ -n "$INTERVAL" ] && ARGS+=(--interval "$INTERVAL")
    return 0
}

build_args

log "Pulse server : $PULSE_URL"
log "NAS          : $(hostname) (DSM ${DSM_MAJOR}, $(uname -m))"
log "Mode         : $MODE"
# Both meaningless for an uninstall, which just tears down whatever is there.
if [ "$MODE" != "uninstall" ]; then
    log "Profile      : root (forced by upstream on appliance platforms)"
    [ "$DISK_EXCLUDE_DEFAULTS" = true ] && log "Excluding    : md0, md1 (DSM system + swap — see header)"
fi

display_args() {
    local out=""
    for a in "${ARGS[@]}"; do
        [ -n "$TOKEN_FILE" ] && [ "$a" = "$TOKEN_FILE" ] && a="[token-file]"
        out="$out $a"
    done
    printf '%s' "$out"
}

if [ "$DRY_RUN" = true ]; then
    log "Dry run — would execute:"
    echo "    curl -fsSL $PULSE_URL/install.sh | bash -s --$(display_args)"
    exit 0
fi

log "Fetching installer from $PULSE_URL/install.sh"
INSTALLER="$(mktemp)"
trap 'cleanup; rm -f "$INSTALLER"' EXIT
curl -fsSL --max-time 60 "$PULSE_URL/install.sh" -o "$INSTALLER" \
    || die "Could not fetch the installer. Is $PULSE_URL reachable from the NAS?"
[ -s "$INSTALLER" ] || die "Installer downloaded empty from $PULSE_URL/install.sh"

bash "$INSTALLER" "${ARGS[@]}" || die "The Pulse installer failed (see output above)."

# ── Verify ──────────────────────────────────────────────────────────────────────
if [ "$MODE" = "uninstall" ]; then
    log "Agent removed from $(hostname)."
    exit 0
fi

if [ "$DSM_MAJOR" -ge 7 ] 2>/dev/null; then
    if systemctl is-active --quiet pulse-agent 2>/dev/null; then
        log "Service pulse-agent is active."
    else
        warn "pulse-agent is not active. Check: systemctl status pulse-agent"
    fi
else
    # DSM 6.x is Upstart, which has no systemctl.
    if initctl status pulse-agent 2>/dev/null | grep -q running; then
        log "Upstart job pulse-agent is running."
    else
        warn "pulse-agent does not appear to be running. Check: initctl status pulse-agent"
    fi
fi

# Confirm the SERVER saw the registration. A running local service that never
# registered is the failure mode worth catching, and it is invisible from here.
CURL_CFG="$(mktemp)"
chmod 600 "$CURL_CFG"
trap 'cleanup; rm -f "$INSTALLER" "$CURL_CFG"' EXIT
printf 'header = "X-API-Token: %s"\n' "$(cat "$TOKEN_FILE")" > "$CURL_CFG"

log "Waiting for $(hostname) to register with Pulse ..."
for _ in $(seq 1 12); do
    if curl -fsS --config "$CURL_CFG" --max-time 10 -o /dev/null \
        "$PULSE_URL/api/agents/agent/lookup?hostname=$(hostname)" 2>/dev/null; then
        log "Registered. The NAS should now be visible in the Pulse UI."
        exit 0
    fi
    sleep 5
done
warn "Not registered after 60s. The agent reports on its own interval, so give it a
       moment; if it stays absent check /var/log/pulse-agent.log on the NAS."
