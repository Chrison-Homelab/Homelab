#!/bin/bash
# proxmox-inventory.sh
# Collects hardware info for Proxmox nodes in a Confluence-friendly format

HOSTNAME=$(hostname)

echo "## Proxmox Host: $HOSTNAME"
echo ""

# CPU Info
echo "### CPU"
lscpu | grep -E 'Model name|Socket|Core|Thread|CPU MHz' | sed 's/^/ - /'

# Memory Info
echo ""
echo "### Memory"
MEM_TOTAL=$(free -h | awk '/Mem:/ {print $2}')
MEM_USED=$(free -h | awk '/Mem:/ {print $3}')
echo " - Total: $MEM_TOTAL"
echo " - Used: $MEM_USED"

# Storage Info
echo ""
echo "### Storage"
lsblk -o NAME,SIZE,TYPE,MOUNTPOINT | grep -E 'disk|part' | sed 's/^/ - /'

# Network Interfaces
echo ""
echo "### Network Interfaces"
ip -o link show | awk -F': ' '{print " - "$2}' 

# PCI Devices (useful for GPU/NIC passthrough)
echo ""
echo "### PCI Devices"
lspci | grep -E 'Ethernet|VGA|Storage|USB' | sed 's/^/ - /'

# System Info
echo ""
echo "### System"
echo " - Manufacturer: $(dmidecode -s system-manufacturer 2>/dev/null)"
echo " - Product Name: $(dmidecode -s system-product-name 2>/dev/null)"
echo " - Serial Number: $(dmidecode -s system-serial-number 2>/dev/null)"
