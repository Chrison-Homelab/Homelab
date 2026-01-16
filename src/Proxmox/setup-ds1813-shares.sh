#!/usr/bin/env bash

set -e

NAS_IP="192.168.179.11"
NAS_NAME="DS1813-01"

# Volumes to mount
VOLUMES=(
  "volume1:/volume1/Volume-1"
  "volume2:/volume2/Volume-2"
  "volume3:/volume3/Volume-3"
  "volume4:/volume4/Volume-4"
)

BASE_MOUNT="/mnt/${NAS_NAME}"

echo "=== Setting up NFS mounts for ${NAS_NAME} (${NAS_IP}) ==="

echo "Creating base directory: ${BASE_MOUNT}"
mkdir -p "${BASE_MOUNT}"

echo "Installing NFS client utilities..."
apt update -y
apt install -y nfs-common

echo "Processing volumes..."
for entry in "${VOLUMES[@]}"; do
    VOL_NAME="${entry%%:*}"
    NAS_EXPORT="${entry#*:}"

    LOCAL_PATH="${BASE_MOUNT}/${VOL_NAME}"

    echo "Creating mountpoint: ${LOCAL_PATH}"
    mkdir -p "${LOCAL_PATH}"

    echo "Testing mount for ${NAS_EXPORT}..."
    mount -t nfs -o vers=4.2,rsize=1048576,wsize=1048576,noatime,nodiratime,async \
        "${NAS_IP}:${NAS_EXPORT}" "${LOCAL_PATH}"

    echo "Unmounting test mount..."
    umount "${LOCAL_PATH}"

    echo "Adding to /etc/fstab if not already present..."
    FSTAB_ENTRY="${NAS_IP}:${NAS_EXPORT}  ${LOCAL_PATH}  nfs  vers=4.2,rsize=1048576,wsize=1048576,noatime,nodiratime,async  0  0"

    grep -qxF "${FSTAB_ENTRY}" /etc/fstab || echo "${FSTAB_ENTRY}" >> /etc/fstab
done

echo "Mounting all filesystems..."
mount -a

echo "Validating mounts..."
df -h | grep "${NAS_NAME}" || echo "Warning: No mounts detected for ${NAS_NAME}"

echo "=== NFS setup complete for ${NAS_NAME} ==="
