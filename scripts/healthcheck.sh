#!/usr/bin/env bash
# healthcheck.sh — post-outage / anytime homelab health check.
#
# Probes every device in healthcheck.hosts (ICMP + key TCP ports) and prints a
# green/red table. Zero dependencies beyond ping and one of nc/python3, so it
# runs from any workstation on the LAN — no secrets, no .NET toolchain.
#
# Usage:
#   ./scripts/healthcheck.sh                 # probe everything
#   ./scripts/healthcheck.sh --public        # also check https://proxmox.chrison.dev
#   ./scripts/healthcheck.sh --hosts FILE     # use a different inventory file
#   ./scripts/healthcheck.sh -q               # quiet: table only, no hints
#
# Remote one-liner (from an always-on node):
#   bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/scripts/healthcheck.sh)
#
# Exit codes: 0 = all critical hosts up · 1 = a critical host is down.
#
# Inventory lives in healthcheck.hosts (see that file's header). Waking a downed
# node is a separate, deliberate step: src/Proxmox/wake-node.sh <node>.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOSTS_FILE="$SCRIPT_DIR/healthcheck.hosts"
PUBLIC_URL="https://proxmox.chrison.dev/api2/json/version"
CHECK_PUBLIC=0
QUIET=0
PING_TIMEOUT=2   # seconds
TCP_TIMEOUT=2    # seconds

usage() { sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --public)  CHECK_PUBLIC=1 ;;
        --hosts)   HOSTS_FILE="${2:?--hosts needs a path}"; shift ;;
        -q|--quiet) QUIET=1 ;;
        -h|--help) usage 0 ;;
        *) echo "Unknown option: $1" >&2; usage 1 ;;
    esac
    shift
done

[ -f "$HOSTS_FILE" ] || { echo "Inventory not found: $HOSTS_FILE" >&2; exit 2; }

# --- colours (only when stdout is a terminal) --------------------------------
if [ -t 1 ]; then
    R=$'\e[31m'; G=$'\e[32m'; Y=$'\e[33m'; DIM=$'\e[2m'; B=$'\e[1m'; N=$'\e[0m'
else
    R=; G=; Y=; DIM=; B=; N=
fi

# --- probes ------------------------------------------------------------------
ping_host() {   # $1=ip -> 0 if replies
    case "$(uname -s)" in
        Darwin) ping -c1 -t"$PING_TIMEOUT" "$1" ;;
        *)      ping -c1 -W"$PING_TIMEOUT" "$1" ;;
    esac >/dev/null 2>&1
}

tcp_open() {    # $1=ip $2=port -> 0 if the TCP port accepts a connection
    local ip="$1" port="$2"
    # python3 first: socket.settimeout() reliably bounds the *connect* on every
    # platform. macOS `nc -w` only bounds idle time, not the SYN, so a host with
    # no ARP entry would stall on the OS timeout (~75s) — hence nc is the fallback
    # and gets -G (connect timeout) when on Darwin.
    if command -v python3 >/dev/null 2>&1; then
        python3 - "$ip" "$port" "$TCP_TIMEOUT" <<'PY'
import socket, sys
ip, port, t = sys.argv[1], int(sys.argv[2]), float(sys.argv[3])
s = socket.socket(); s.settimeout(t)
sys.exit(0 if s.connect_ex((ip, port)) == 0 else 1)
PY
    elif command -v nc >/dev/null 2>&1; then
        if [ "$(uname -s)" = "Darwin" ]; then
            nc -z -G "$TCP_TIMEOUT" -w "$TCP_TIMEOUT" "$ip" "$port" >/dev/null 2>&1
        else
            nc -z -w "$TCP_TIMEOUT" "$ip" "$port" >/dev/null 2>&1
        fi
    else
        echo "need python3 or nc for TCP checks" >&2; return 2
    fi
}

# --- run ---------------------------------------------------------------------
printf '%s%s Homelab health check %s %s(%s)%s\n\n' \
    "$B" "🩺" "$N" "$DIM" "$(uname -n)" "$N"
printf '%b\n' "  ${DIM}STATUS   HOST            IP                ROLE      DETAIL${N}"

crit_down=0; warn_down=0; total_up=0; total=0

while read -r name ip role ports severity _rest; do
    [ -z "${name:-}" ] && continue
    case "$name" in \#*) continue ;; esac
    total=$((total + 1))

    icmp=""; ping_host "$ip" && icmp="ping" || icmp=""

    port_detail=""; any_port_up=0; had_ports=0
    if [ "$ports" != "-" ]; then
        had_ports=1
        IFS=',' read -ra plist <<< "$ports"
        for p in "${plist[@]}"; do
            if tcp_open "$ip" "$p"; then
                port_detail+="${G}${p}↑${N} "; any_port_up=1
            else
                port_detail+="${R}${p}↓${N} "
            fi
        done
    fi

    # UP = a listed port answered, or (no ports defined) ICMP replied.
    if { [ "$had_ports" = 1 ] && [ "$any_port_up" = 1 ]; } || \
       { [ "$had_ports" = 0 ] && [ -n "$icmp" ]; }; then
        up=1
    else
        up=0
    fi

    detail="$port_detail"
    [ -n "$icmp" ] && detail+="${DIM}ping${N}" || detail+="${DIM}no-ping${N}"

    if [ "$up" = 1 ]; then
        icon="${G}🟢 UP${N}  "; total_up=$((total_up + 1))
    elif [ "$severity" = "optional" ]; then
        icon="${Y}🟡 WARN${N}"; warn_down=$((warn_down + 1))
    else
        icon="${R}🔴 DOWN${N}"; crit_down=$((crit_down + 1))
    fi

    printf '%b  %-14s  %-15s   %-8s  %b\n' "$icon" "$name" "$ip" "$role" "$detail"
done < "$HOSTS_FILE"

# --- optional public endpoint ------------------------------------------------
if [ "$CHECK_PUBLIC" = 1 ]; then
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 "$PUBLIC_URL" 2>/dev/null || echo 000)"
    if [ "$code" = "200" ] || [ "$code" = "401" ]; then
        printf '%b  %-14s  %-15s   %-8s  %bHTTP %s%b\n' "${G}🟢 UP${N}  " "public" "proxmox.chrison.dev" "ingress" "$DIM" "$code" "$N"
    else
        printf '%b  %-14s  %-15s   %-8s  %bHTTP %s (tunnel/origin down)%b\n' "${R}🔴 DOWN${N}" "public" "proxmox.chrison.dev" "ingress" "$DIM" "$code" "$N"
    fi
fi

# --- summary -----------------------------------------------------------------
echo
if [ "$crit_down" -eq 0 ] && [ "$warn_down" -eq 0 ]; then
    printf '%b✅ All %d hosts up.%b\n' "$G$B" "$total" "$N"
elif [ "$crit_down" -eq 0 ]; then
    printf '%b✅ All critical hosts up%b — %d optional host(s) down.\n' "$G$B" "$N" "$warn_down"
else
    printf '%b🔴 %d critical host(s) DOWN%b (%d/%d up).\n' "$R$B" "$crit_down" "$N" "$total_up" "$total"
    if [ "$QUIET" = 0 ]; then
        echo
        printf '%bNext steps:%b\n' "$B" "$N"
        echo "  • Node powered but unreachable → likely BIOS/POST hang; needs a monitor+keyboard."
        echo "  • Node fully off with WoL armed → wake from an up node:"
        echo "      ssh root@<up-node> 'bash src/Proxmox/wake-node.sh <down-node>'"
        echo "  • NAS down → check DS1813 front panel / PSU; NFS mounts on nodes depend on it."
    fi
fi

[ "$crit_down" -eq 0 ]
