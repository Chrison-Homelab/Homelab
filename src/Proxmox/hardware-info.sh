#!/bin/bash
# hardware-info.sh
# 
# Collects vendor and model information for key hardware components on a Proxmox host
# Provides a quick overview of CPU, mainboard, RAM, graphics cards, and network interfaces
#
# Usage: ./hardware-info.sh
# Requirements: lscpu, dmidecode, lspci

echo "=== Hardware Information for $(hostname) ==="

# CPU
CPU_MODEL=$(lscpu | grep "Model name" | sed 's/Model name:\s*//')
echo "CPU: $CPU_MODEL"

# Mainboard
MB_VENDOR=$(dmidecode -s baseboard-manufacturer 2>/dev/null)
MB_MODEL=$(dmidecode -s baseboard-product-name 2>/dev/null)
echo "Mainboard: ${MB_VENDOR} ${MB_MODEL}"

# RAM
RAM_INFO=$(dmidecode -t memory 2>/dev/null | grep -A5 "Memory Device" | grep "Size:" | grep -v "No Module Installed" | awk '{print $2" "$3}' | paste -sd " + ")
echo "RAM: $RAM_INFO"

# Graphics Card(s)
GPU_INFO=$(lspci | grep -i 'vga\|3d\|display' | sed 's/^[0-9a-f:.]* //')
echo "Graphics: $GPU_INFO"

# NIC(s)
NIC_INFO=$(lspci | grep -i 'ethernet' | sed 's/^[0-9a-f:.]* //')
echo "NICs: $NIC_INFO"

echo "==========================================="