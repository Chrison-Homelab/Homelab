#!/bin/bash

echo "=== Hostname and OS ==="
hostnamectl

echo -e "\n=== CPU Info ==="
lscpu

echo -e "\n=== Memory Info ==="
free -h
echo -e "\nDetailed Memory:"
dmidecode -t memory | grep -E "Size:|Speed:|Locator:|Type:"

echo -e "\n=== Storage Devices ==="
lsblk -o NAME,SIZE,TYPE,MOUNTPOINT
echo -e "\nDisk Details:"
for disk in /dev/sd?; do
  echo -e "\n$disk:"
  smartctl -i "$disk" | grep -E "Model|Size|Serial|Rotation"
done

echo -e "\n=== Network Interfaces ==="
ip -brief address
echo -e "\nInterface Details:"
for iface in $(ls /sys/class/net); do
  echo -e "\n$iface:"
  ethtool "$iface" | grep -E "Speed|Duplex|Auto-negotiation"
done

echo -e "\n=== GPU Info ==="
lspci | grep -i vga
echo -e "\nIntel GPU (if present):"
ls /dev/dri/* 2>/dev/null && vainfo 2>/dev/null || echo "No Intel GPU or VAAPI not installed"

echo -e "\n=== Uptime and Load ==="
uptime

echo -e "\n=== Proxmox Version ==="
pveversion

echo -e "\n=== ZFS Pools (if any) ==="
zpool list 2>/dev/null || echo "No ZFS pools detected"

echo -e "\n=== PCI Devices ==="
lspci | grep -iE "ethernet|storage|usb|sata|nvme"

echo -e "\n=== USB Devices ==="
lsusb