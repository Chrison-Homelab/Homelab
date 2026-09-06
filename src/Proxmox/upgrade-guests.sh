#!/usr/bin/env bash
# upgrade-guests.sh — apply pending OS package upgrades inside every RUNNING LXC on this node.
#
# Runs ON a Proxmox node (fetch-and-run, like the other scripts here):
#   ssh root@hpe-01.homelab.chrison.internal 'bash -s' < src/Proxmox/upgrade-guests.sh
#   ssh root@hpe-01.homelab.chrison.internal 'bash -s -- --dry-run' < src/Proxmox/upgrade-guests.sh
#   ssh root@hpe-01.homelab.chrison.internal 'bash -s -- --reboot' < src/Proxmox/upgrade-guests.sh
#
#   --dry-run   refresh indexes and REPORT pending/security/reboot-required per guest; install nothing
#   --reboot    after upgrading, reboot the guests that flag /var/run/reboot-required
#   --only ID[,ID]  restrict to these CT ids
#
# Scope is deliberately OS PACKAGES ONLY (apt). community-scripts apps (Sonarr, Home Assistant, …)
# update through their own `update` command; container images update through podman auto-update
# or the docker member's update path; the node itself is `apt dist-upgrade` + a planned reboot.
# Those stay separate, deliberate acts — this is the boring, safe layer (#436).
#
# Non-interactive: keeps the local config file on conflicts (confold), so a guest's hand-tuned
# /etc survives. Each guest's full apt output lands in its own /var/log/homelab-upgrade.log.
set -euo pipefail
DRY=0; REBOOT=0; ONLY=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) DRY=1 ;;
    --reboot) REBOOT=1 ;;
    --only) ONLY="$2"; shift ;;
    *) echo "unknown arg $1" >&2; exit 2 ;;
  esac; shift
done

ids=$(pct list | awk 'NR>1 && $2=="running"{print $1}')
[[ -n "$ONLY" ]] && ids=$(tr ',' '\n' <<<"$ONLY")

printf '%-6s %-24s %-8s %-9s %s\n' CT NAME PENDING SECURITY STATE
total=0; sec_total=0; rebooters=()
for id in $ids; do
  name=$(pct config "$id" | awk '/^hostname:/{print $2}')
  if [[ $DRY -eq 1 ]]; then
    out=$(pct exec "$id" -- bash -c '
      apt-get update -qq >/dev/null 2>&1
      up=$(apt list --upgradable 2>/dev/null | grep -c "/"); sec=$(apt list --upgradable 2>/dev/null | grep -ci security)
      rb=""; [ -f /var/run/reboot-required ] && rb="reboot-required"
      echo "$up $sec $rb"' 2>/dev/null || echo "? ? exec-failed")
  else
    out=$(pct exec "$id" -- bash -c '
      export DEBIAN_FRONTEND=noninteractive
      apt-get update -qq >/dev/null 2>&1
      before=$(apt list --upgradable 2>/dev/null | grep -c "/"); sec=$(apt list --upgradable 2>/dev/null | grep -ci security)
      apt-get -qq -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold dist-upgrade >/var/log/homelab-upgrade.log 2>&1; rc=$?
      apt-get -qq -y autoremove >>/var/log/homelab-upgrade.log 2>&1 || true
      left=$(apt list --upgradable 2>/dev/null | grep -c "/")
      rb=""; [ -f /var/run/reboot-required ] && rb="reboot-required"
      st="upgraded"; [ $rc -ne 0 ] && st="FAILED(rc=$rc)"; [ "$left" != 0 ] && st="$st left=$left"
      echo "$before $sec $st $rb"' 2>/dev/null || echo "? ? exec-failed")
  fi
  read -r up sec rest <<<"$out"
  printf '%-6s %-24s %-8s %-9s %s\n' "$id" "$name" "$up" "$sec" "$rest"
  [[ "$up" =~ ^[0-9]+$ ]] && total=$((total+up)) && sec_total=$((sec_total+sec))
  [[ "$rest" == *reboot-required* ]] && rebooters+=("$id")
done
echo "---- $(wc -w <<<"$ids") guest(s): $total pending ($sec_total security) $( [[ $DRY -eq 1 ]] && echo 'reported' || echo 'processed')"
if [[ ${#rebooters[@]} -gt 0 ]]; then
  echo "reboot required: ${rebooters[*]}"
  if [[ $REBOOT -eq 1 && $DRY -eq 0 ]]; then
    for id in "${rebooters[@]}"; do echo "rebooting CT $id"; pct reboot "$id" || pct stop "$id" && pct start "$id"; done
  fi
fi
