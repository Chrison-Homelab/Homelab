#!/bin/bash
# proxmox-cpu-snapshot.sh
# 
# Collects CPU configuration and usage statistics for VMs and LXC containers
# Useful for capacity planning, performance analysis, and resource optimization
#
# Usage: ./proxmox-cpu-snapshot.sh
# Requirements: qm (QEMU/KVM management), pct (Proxmox Container Toolkit), mpstat

NODE=$(hostname)

echo "=== Proxmox CPU Snapshot on $NODE ==="
echo "Timestamp: $(date)"
echo

echo "---- VM Configurations ----"
for VMID in $(qm list | awk 'NR>1 {print $1}'); do
    echo "VM $VMID:"
    qm config $VMID | egrep "cpu:|cores:|cpulimit:|cpuunits:"
    qm status $VMID --verbose | grep cpu
    echo
done

echo "---- LXC Configurations ----"
for CTID in $(pct list | awk 'NR>1 {print $1}'); do
    echo "CT $CTID:"
    pct config $CTID | egrep "cores:|cpulimit:|cpuunits:"
    pct status $CTID | grep cpu
    echo
done

echo "---- Host CPU Usage (per thread) ----"
mpstat -P ALL 1 1 | awk '/Average/ && $2 ~ /[0-9]/ {printf "CPU%-2s: %5.1f%%\n", $2, 100-$12}'
