#!/usr/bin/env bash
# Proxmox CT lifecycle hookscript — BL-016 shared-export guard (issue #105).
#
# A shared NFS export bound by *path* (not a storage-referenced volume) is NOT
# gated by Proxmox on storage health. If the NAS drops, the bind target becomes
# an empty dir and writes land on the CT rootfs — the documented CT 5007
# rootfs-fill failure mode. This pre-start hook refuses to start a member unless
# the volume4 export is genuinely mounted on the host, closing that race.
#
# Install on a snippets-enabled storage and reference from a shape as:
#   spec.hookscript: local:snippets/ensure-data-mount.sh
# (see docs/runbooks/volume4-data-export.md). Must be executable: chmod 755.
#
# Proxmox invokes: <script> <vmid> <phase>   phase ∈ pre-start|post-start|pre-stop|post-stop
set -euo pipefail

vmid="${1:?vmid}"
phase="${2:?phase}"

HOST_MOUNT="/mnt/pve/ds1813-nfs-volume-4"
DATA_DIR="${HOST_MOUNT}/data"

case "$phase" in
  pre-start)
    if ! mountpoint -q "$HOST_MOUNT"; then
      echo "[ensure-data-mount] $HOST_MOUNT is not mounted — refusing to start CT $vmid" >&2
      echo "[ensure-data-mount] (would write to the CT rootfs instead of the NAS)" >&2
      exit 1
    fi
    if [ ! -d "$DATA_DIR" ]; then
      echo "[ensure-data-mount] $DATA_DIR missing on a mounted export — refusing to start CT $vmid" >&2
      exit 1
    fi
    ;;
  *)
    : # nothing to do for post-start / pre-stop / post-stop
    ;;
esac

exit 0
