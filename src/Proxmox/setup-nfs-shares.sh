#!/usr/bin/env bash
# setup-nfs-shares.sh
#
# Dynamically discovers and mounts NFS exports from a NAS to a Proxmox node
# Automatically creates mount points and persists them in /etc/fstab
# 
# Usage: ./setup-nfs-shares.sh [NAS_IP] [NAS_NAME]
#   NAS_IP: address of the NAS — the UniFi name by default (default: nas.homelab.chrison.internal)
#   NAS_NAME: Name for the mount base directory (default: DS1813-01)
#
# Example: ./setup-nfs-shares.sh 192.168.1.100 MyNAS
#
# Requirements: showmount, nfs-common package

set -e

NAS_IP="${1:-nas.homelab.chrison.internal}"   # by UniFi name; the NAS moved to VLAN 1000 (#37)
NAS_NAME="${2:-DS1813-01}"
BASE_MOUNT="/mnt/${NAS_NAME}"

# NFS mount options
# If you want to use NFSv4, you must:
# • enable NFSv4 on the NAS
# • set the NFSv4 domain
# • configure /etc/idmapd.conf on Proxmox
NFS_VERSION=3
NFS_MOUNT_OPTIONS="vers=${NFS_VERSION},rsize=1048576,wsize=1048576,noatime,nodiratime,async"

echo "=== Discovering NFS exports from ${NAS_IP} ==="

EXPORTS=$(showmount -e ${NAS_IP} | tail -n +2 | awk '{print $1}')

if [ -z "$EXPORTS" ]; then
    echo "No NFS exports found on ${NAS_IP}"
    exit 1
fi

echo "Found exports:"
echo "$EXPORTS"

# TODO: Exclude exports on the wrong subnet
# This requires additional logic to determine the subnet of the Proxmox host
# and compare it with the export paths if they contain subnet information.
# For now, we will mount all discovered exports.
# TODO: Add filtering logic here if needed
# For example, to exclude certain exports:
# FILTERED_EXPORTS=""
# for EXPORT in $EXPORTS; do
#     if [[ "$EXPORT" != *"excluded_pattern"* ]]; then
#         FILTERED_EXPORTS+="$EXPORT "
#     fi
# done
# EXPORTS="$FILTERED_EXPORTS"

echo "Creating base directory: ${BASE_MOUNT}"
mkdir -p "${BASE_MOUNT}"

echo "Installing NFS client utilities..."
apt update -y
apt install -y nfs-common

for EXPORT in $EXPORTS; do
    # Extract the last part of the export path as the local folder name
    # Example: /volume3/Volume-3 → Volume-3
    LOCAL_NAME=$(basename "$EXPORT")
    LOCAL_PATH="${BASE_MOUNT}/${LOCAL_NAME}"

    echo "Processing export: ${EXPORT}"
    echo "Local mountpoint: ${LOCAL_PATH}"

    mkdir -p "${LOCAL_PATH}"

    # Test mount
    echo "Testing mount..."
    mount -t nfs -o ${NFS_MOUNT_OPTIONS} \
        "${NAS_IP}:${EXPORT}" "${LOCAL_PATH}"

    echo "Unmounting test mount..."
    umount "${LOCAL_PATH}"

    # Persist mount in /etc/fstab if mount test was successful
    echo "Persisting mount in /etc/fstab..."
    

    # Add to /etc/fstab if not already present
    FSTAB_ENTRY="${NAS_IP}:${EXPORT}  ${LOCAL_PATH}  nfs  ${NFS_MOUNT_OPTIONS}  0  0"

    echo "Adding to /etc/fstab if missing..."
    grep -qxF "${FSTAB_ENTRY}" /etc/fstab || echo "${FSTAB_ENTRY}" >> /etc/fstab
done

echo "Mounting all filesystems..."
mount -a

echo "Validating mounts..."
df -h | grep "${NAS_NAME}" || echo "Warning: No mounts detected for ${NAS_NAME}"

echo "=== Dynamic NFS setup complete for ${NAS_NAME} ==="
