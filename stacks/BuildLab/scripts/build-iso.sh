#!/usr/bin/env bash
# Bake the fully-unattended Windows 11 install ISO for BuildLab buildvm (VMID 1100)
# on a Linux host (e.g. run it directly ON the Proxmox node). PowerShell equivalent:
# build-iso.ps1 (preferred on Windows). See ../README.md.
#
# Adds to a stock Win11 ISO:
#   /autounattend.xml            — the silent-install answer file (this stack)
#   /virtio/                     — virtio-win drivers (WinPE needs the virtio-scsi one)
#   /BuildLab/                   — provision-vs.ps1 + *.vsconfig + guest tools (ISO ROOT;
#                                  a first-logon cmd copies it → C:\BuildLab)
#
# The homelab engine has NO ISO build logic — this is the documented prerequisite
# before `./build.sh Deploy --stack BuildLab`.
#
# Requires: xorriso, 7z (p7zip) or bsdtar to extract the source ISOs, wget/curl.
#   apt-get install -y xorriso p7zip-full wget
#
# Usage:
#   ./build-iso.sh -w /path/Win11_x64.iso [-v /path/virtio-win.iso] \
#                  [-o /var/lib/vz/template/iso/buildlab-win11-unattended.iso]
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"   # stacks/BuildLab
UNATTEND="$HERE/unattend"
VIRTIO_URL="https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso"
ISO_LABEL="BUILDLAB_W11"

WIN_ISO=""; VIRTIO_ISO=""; OUT_ISO=""
while getopts "w:v:o:h" opt; do
  case "$opt" in
    w) WIN_ISO="$OPTARG" ;;
    v) VIRTIO_ISO="$OPTARG" ;;
    o) OUT_ISO="$OPTARG" ;;
    h) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "see -h" >&2; exit 2 ;;
  esac
done
[ -n "$WIN_ISO" ] || { echo "ERROR: -w <Win11.iso> is required" >&2; exit 2; }
OUT_ISO="${OUT_ISO:-/var/lib/vz/template/iso/buildlab-win11-unattended.iso}"

command -v xorriso >/dev/null || { echo "ERROR: xorriso not found (apt install xorriso)" >&2; exit 1; }
extract() { # extract <iso> <dest>
  if command -v 7z >/dev/null; then 7z x -y -o"$2" "$1" >/dev/null
  elif command -v bsdtar >/dev/null; then mkdir -p "$2" && bsdtar -C "$2" -xf "$1"
  else echo "ERROR: need 7z or bsdtar to extract ISOs" >&2; exit 1; fi
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
echo "==> Work dir: $WORK"

if [ -z "$VIRTIO_ISO" ]; then
  VIRTIO_ISO="$WORK/virtio-win.iso"
  echo "==> Downloading virtio-win.iso ..."
  if command -v wget >/dev/null; then wget -qO "$VIRTIO_ISO" "$VIRTIO_URL"
  else curl -fsSL -o "$VIRTIO_ISO" "$VIRTIO_URL"; fi
fi

SRC="$WORK/src"
echo "==> Extracting base Windows ISO ..."
extract "$WIN_ISO" "$SRC"
chmod -R u+w "$SRC"

echo "==> Injecting autounattend.xml + virtio drivers + guest payload ..."
cp "$UNATTEND/autounattend.xml" "$SRC/autounattend.xml"
extract "$VIRTIO_ISO" "$SRC/virtio"

OEM="$SRC/BuildLab"   # ISO ROOT — 24H2 setup no longer stages sources\$OEM\$; a first-logon cmd copies this off the CD
mkdir -p "$OEM"
cp "$UNATTEND/provision-vs.ps1" "$OEM/"
cp "$UNATTEND"/*.vsconfig        "$OEM/"
[ -f "$SRC/virtio/virtio-win-guest-tools.exe" ] && cp "$SRC/virtio/virtio-win-guest-tools.exe" "$OEM/"

echo "==> Building UEFI+BIOS bootable ISO: $OUT_ISO"
# Windows install media carries both boot images: boot/etfsboot.com (BIOS) and
# efi/microsoft/boot/efisys.bin (UEFI). Reproduce a dual El Torito catalog.
xorriso -as mkisofs \
  -iso-level 3 -full-iso9660-filenames -volid "$ISO_LABEL" \
  -b boot/etfsboot.com -no-emul-boot -boot-load-size 8 -boot-info-table \
  -eltorito-alt-boot -e efi/microsoft/boot/efisys_noprompt.bin -no-emul-boot \
  -o "$OUT_ISO" "$SRC"

echo "==> Built $OUT_ISO"
echo "    If you ran this off-node, copy it to <node>:/var/lib/vz/template/iso/"
echo "    Then: ./build.sh Deploy --stack BuildLab"
