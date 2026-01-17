#!/usr/bin/env bash

set -e

NAS_IP="${1:-192.168.179.11}"
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

    echo "Testing mount..."
    mount -t nfs -o ${NFS_MOUNT_OPTIONS} \
        "${NAS_IP}:${EXPORT}" "${LOCAL_PATH}"

    echo "Unmounting test mount..."
    umount "${LOCAL_PATH}"

    FSTAB_ENTRY="${NAS_IP}:${EXPORT}  ${LOCAL_PATH}  nfs  ${NFS_MOUNT_OPTIONS}  0  0"

    echo "Adding to /etc/fstab if missing..."
    grep -qxF "${FSTAB_ENTRY}" /etc/fstab || echo "${FSTAB_ENTRY}" >> /etc/fstab
done

echo "Mounting all filesystems..."
mount -a

echo "Validating mounts..."
df -h | grep "${NAS_NAME}" || echo "Warning: No mounts detected for ${NAS_NAME}"

echo "=== Dynamic NFS setup complete for ${NAS_NAME} ==="
