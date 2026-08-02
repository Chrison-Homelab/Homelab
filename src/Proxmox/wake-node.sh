#!/bin/bash
# wake-node.sh
#
# Sends a Wake-on-LAN magic packet to bring a sleeping Proxmox node back up.
# Designed to run from an always-on node (e.g. nuc-01/hpe-01) so heavy nodes
# (desktop-01, hpe-02) can be powered down when idle and woken on demand.
#
# The target NIC must have WoL armed (see wol-arm.service / `ethtool -s <if> wol g`)
# and BIOS WoL enabled. Sender and target must share an L2 broadcast domain, OR
# you must pass a directed subnet broadcast via -b.
#
# Usage:
#   ./wake-node.sh <node-name|MAC>            # wake by known node name or raw MAC
#   ./wake-node.sh desktop-01                 # known node (see NODE_MACS below)
#   ./wake-node.sh 18:c0:4d:de:9f:82          # raw MAC
#   ./wake-node.sh -b 10.0.255.255 desktop-01      # directed broadcast (cross-subnet; nodes are on VLAN 1000 since #37)
#   ./wake-node.sh -p 7 desktop-01            # custom UDP port (default 9)
#
# Remote one-liner:
#   bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/wake-node.sh) desktop-01
#
# Requirements: one of wakeonlan / etherwake / python3 (python3 needs no extra pkg).

set -euo pipefail

BROADCAST="255.255.255.255"
PORT=9

# Known node → MAC registry. Add nodes here as WoL is armed on them.
declare -A NODE_MACS=(
    ["desktop-01"]="18:c0:4d:de:9f:82"
    ["hpe-01"]="c8:d3:ff:9d:da:02"
    ["nuc-01"]="b8:ae:ed:72:82:fe"
)

usage() { sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

while getopts ":b:p:h" opt; do
    case "$opt" in
        b) BROADCAST="$OPTARG" ;;
        p) PORT="$OPTARG" ;;
        h) usage 0 ;;
        *) echo "Unknown option: -$OPTARG" >&2; usage 1 ;;
    esac
done
shift $((OPTIND - 1))

[ $# -eq 1 ] || { echo "Error: expected exactly one node name or MAC" >&2; usage 1; }
TARGET="$1"

# Resolve a known node name to its MAC; otherwise treat the argument as a MAC.
MAC="${NODE_MACS[$TARGET]:-$TARGET}"

# Normalise + validate MAC (accept : or - separators, store as colon-separated).
MAC="${MAC//-/:}"
if ! [[ "$MAC" =~ ^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$ ]]; then
    echo "Error: '$TARGET' is not a known node and not a valid MAC (aa:bb:cc:dd:ee:ff)." >&2
    echo "Known nodes: ${!NODE_MACS[*]}" >&2
    exit 1
fi

echo "Waking $TARGET ($MAC) via $BROADCAST:$PORT ..."

if command -v wakeonlan >/dev/null 2>&1; then
    wakeonlan -i "$BROADCAST" -p "$PORT" "$MAC"
elif command -v etherwake >/dev/null 2>&1; then
    etherwake "$MAC"
elif command -v python3 >/dev/null 2>&1; then
    python3 - "$MAC" "$BROADCAST" "$PORT" <<'PY'
import socket, sys
mac, bcast, port = sys.argv[1], sys.argv[2], int(sys.argv[3])
packet = b'\xff' * 6 + bytes.fromhex(mac.replace(':', '')) * 16
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
s.sendto(packet, (bcast, port))
s.close()
print("Magic packet sent.")
PY
else
    echo "Error: need one of wakeonlan, etherwake, or python3 to send the packet." >&2
    exit 1
fi

echo "Done. Give the node ~30-60s to POST and rejoin the cluster."
