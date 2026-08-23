#!/usr/bin/env bash
# install-pulse-agent.sh
#
# Installs, updates or removes the Pulse unified agent on a Proxmox VE node.
#
# The Proxmox API alone already gives Pulse the guest inventory, cluster state and
# storage/backup view. The agent exists for what the API physically cannot return:
# per-disk S.M.A.R.T. health, CPU/NVMe temperatures, ZFS/mdadm/Ceph detail, and the
# mounted-filesystem breakdown inside LXCs. That gap is what the "Host telemetry not
# installed" banner in the Pulse UI is pointing at.
#
# This is a thin, opinionated wrapper around the installer the Pulse SERVER itself
# serves at $PULSE_URL/install.sh — the agent is always version-matched to the server
# that way, so there is no vendored copy in this repo to drift.
#
# ── SECRETS ─────────────────────────────────────────────────────────────────────
# The API token is NEVER passed on the command line: argv is world-readable via
# /proc on a shared node. Supply it one of these ways (checked in order):
#
#   1. PULSE_API_TOKEN in the environment   ← what secrets.env gives you
#   2. --token-file <path>                  ← a mode-600 file
#   3. --token-stdin                        ← piped in
#
# From a checkout, the environment route is:
#   set -a && . ./secrets.env && set +a
#
# ── USAGE ───────────────────────────────────────────────────────────────────────
#   ./install-pulse-agent.sh                      # install (or update if present)
#   ./install-pulse-agent.sh --update             # re-run using saved connection state
#   ./install-pulse-agent.sh --uninstall          # remove agent + deregister
#   ./install-pulse-agent.sh --dry-run            # print the command, change nothing
#   ./install-pulse-agent.sh --url http://pulse:7655
#   ./install-pulse-agent.sh --interval 15s
#   ./install-pulse-agent.sh --no-commands        # observe-only (see PRIVILEGE below)
#
# Remote one-liner (token still comes from the environment, not the URL):
#   PULSE_API_TOKEN=... bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/install-pulse-agent.sh)
#
# ── PRIVILEGE ───────────────────────────────────────────────────────────────────
# The agent runs as ROOT here, deliberately. Upstream offers --least-privilege
# (a dedicated pulse-agent user + scoped sudoers grants), but the installer rejects
# it together with --enable-commands — the low-privilege profile never receives the
# CAP_SETUID/CAP_SETGID ambient grant, so it cannot lxc-attach into guests, which is
# what Docker-in-LXC inventory and Patrol remediation need. We chose commands.
# Pass --no-commands to drop to observe-only; this script will then install the
# least-privilege profile with SMART + pct grants instead.
#
# Requirements: root on a PVE node, curl, and reachability to the Pulse server.

set -euo pipefail

PULSE_URL="${PULSE_URL:-http://monitoring.homelab.chrison.internal:7655}"
INTERVAL=""
ENABLE_COMMANDS=true
TOKEN_FILE=""
TOKEN_STDIN=false
MODE="install"
DRY_RUN=false
SKIP_PREREQS=false
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
        --update)       MODE="update"; shift ;;
        --uninstall)    MODE="uninstall"; shift ;;
        --dry-run)      DRY_RUN=true; shift ;;
        --no-prereqs)   SKIP_PREREQS=true; shift ;;
        --force)        FORCE=true; shift ;;
        -h|--help)      usage 0 ;;
        --token)
            die "--token is refused on purpose: argv is world-readable via /proc.
       Use PULSE_API_TOKEN in the environment, --token-file, or --token-stdin." ;;
        *) echo "Unknown option: $1" >&2; usage 1 ;;
    esac
done

[ "$(id -u)" -eq 0 ] || die "Must run as root (the agent installs a system service)."

# Refuse to run somewhere that isn't a PVE node unless explicitly forced — this
# script's flags (--enable-proxmox) are meaningless elsewhere.
if [ "$FORCE" = false ] && [ ! -d /etc/pve ] && ! command -v pveversion >/dev/null 2>&1; then
    die "This does not look like a Proxmox VE node (no /etc/pve, no pveversion). Use --force to override."
fi

command -v curl >/dev/null 2>&1 || die "curl is required."

# ── Resolve the token into a mode-600 file ──────────────────────────────────────
# Upstream accepts --token-file, so the secret never reaches argv or the process
# table. Anything we create ourselves is shredded on exit.
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
        die "No API token. Set PULSE_API_TOKEN (e.g. 'set -a && . ./secrets.env && set +a'),
       or pass --token-file <path> / --token-stdin."
    fi
    [ -n "$token" ] || die "Empty API token."
    OWN_TOKEN_FILE="$(mktemp)"
    chmod 600 "$OWN_TOKEN_FILE"
    printf '%s' "$token" > "$OWN_TOKEN_FILE"
    TOKEN_FILE="$OWN_TOKEN_FILE"
}

# Uninstall needs no token — it reads the agent's saved connection state.
[ "$MODE" = "uninstall" ] || resolve_token

# ── Prerequisites for the telemetry that justifies the agent ────────────────────
# Without smartmontools there is no S.M.A.R.T.; without lm-sensors, no temperatures.
# Both are the entire reason for installing an agent on a hypervisor, so a missing
# package is a silently degraded install rather than a loud failure.
install_prereqs() {
    [ "$SKIP_PREREQS" = false ] || return 0
    local missing=()
    command -v smartctl >/dev/null 2>&1 || missing+=(smartmontools)
    command -v sensors  >/dev/null 2>&1 || missing+=(lm-sensors)
    [ ${#missing[@]} -gt 0 ] || { log "Prerequisites present (smartctl, sensors)."; return 0; }
    log "Installing prerequisites: ${missing[*]}"
    if [ "$DRY_RUN" = true ]; then
        echo "    [dry-run] apt-get install -y ${missing[*]}"
        return 0
    fi
    DEBIAN_FRONTEND=noninteractive apt-get update -qq || warn "apt-get update failed; continuing."
    DEBIAN_FRONTEND=noninteractive apt-get install -y "${missing[@]}" \
        || warn "Could not install ${missing[*]} — SMART and/or temperature data will be missing."
}

# ── Build the upstream installer arguments ──────────────────────────────────────
build_args() {
    ARGS=(--url "$PULSE_URL")
    case "$MODE" in
        uninstall) ARGS+=(--uninstall); return ;;
        update)    ARGS+=(--update --token-file "$TOKEN_FILE") ;;
        install)   ARGS+=(--token-file "$TOKEN_FILE") ;;
    esac
    ARGS+=(--enable-proxmox)
    if [ "$ENABLE_COMMANDS" = true ]; then
        # Root profile. Needed for Patrol actions and Docker-in-LXC inventory.
        ARGS+=(--enable-commands)
    else
        # Mutually exclusive with the above — see the PRIVILEGE note in the header.
        ARGS+=(--least-privilege --grant-smart --grant-pct)
    fi
    [ -n "$INTERVAL" ] && ARGS+=(--interval "$INTERVAL")
    return 0
}

build_args
install_prereqs

log "Pulse server : $PULSE_URL"
log "Node         : $(hostname)"
log "Mode         : $MODE"
# Meaningless for an uninstall, which just tears down whatever profile is there.
if [ "$MODE" != "uninstall" ]; then
    log "Profile      : $([ "$ENABLE_COMMANDS" = true ] && echo 'root (command execution enabled)' || echo 'least-privilege (+smart,+pct grants)')"
fi

# Render the args for display with the token path masked, so --dry-run output and
# any pasted terminal log stay safe to share.
display_args() {
    local out=""
    for a in "${ARGS[@]}"; do
        [ "$a" = "$TOKEN_FILE" ] && a="[token-file]"
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
    || die "Could not fetch the installer. Is $PULSE_URL reachable from this node?"
[ -s "$INSTALLER" ] || die "Installer downloaded empty from $PULSE_URL/install.sh"

bash "$INSTALLER" "${ARGS[@]}" || die "The Pulse installer failed (see output above)."

# ── Verify ──────────────────────────────────────────────────────────────────────
if [ "$MODE" = "uninstall" ]; then
    log "Agent removed from $(hostname)."
    exit 0
fi

if systemctl is-active --quiet pulse-agent 2>/dev/null; then
    log "Service pulse-agent is active."
else
    warn "pulse-agent is not active. Check: systemctl status pulse-agent; journalctl -u pulse-agent -n 50"
fi

# Confirm the SERVER actually saw the registration — a running local service that
# never registered is the failure mode worth catching, and it is invisible locally.
#
# The lookup is authenticated, so the token goes into a mode-600 curl config file
# (--config) rather than an -H argument, for the same argv reason as everywhere else.
CURL_CFG="$(mktemp)"
chmod 600 "$CURL_CFG"
trap 'cleanup; rm -f "$INSTALLER" "$CURL_CFG"' EXIT
printf 'header = "X-API-Token: %s"\n' "$(cat "$TOKEN_FILE")" > "$CURL_CFG"

log "Waiting for $(hostname) to register with Pulse ..."
for _ in $(seq 1 12); do
    if curl -fsS --config "$CURL_CFG" --max-time 10 -o /dev/null \
        "$PULSE_URL/api/agents/agent/lookup?hostname=$(hostname)" 2>/dev/null; then
        log "Registered. Host telemetry should now be live in the Pulse UI."
        exit 0
    fi
    sleep 5
done
warn "Not registered after 60s. The agent reports on its own interval, so give it a
       moment; if it stays absent check 'journalctl -u pulse-agent -n 50' on this node."
