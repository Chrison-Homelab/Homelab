# Devices

Hardware inventory for all homelab physical devices.
**Last updated:** 2026-04-05

---

## Proxmox Hosts

### hpe-01 — HP EliteDesk 800 G2 DM
- **Role:** Primary workload host (media stack, smart home, infrastructure)
- **IP:** 192.168.179.3 (legacy — migration pending)
- **CPU:** 4 cores
- **RAM:** 15.5 GB
- **Local Storage:** ~94 GB (local-lvm), ~320 GB (local-lvm thin pool)
- **NAS Storage:** ds1813-nfs-volume-1/2/3 (shared)
- **Uptime:** ~29.7 days

### nuc-01 — Intel NUC D34010WYK
- **Role:** Secondary workload host (arr-stack overflow, infrastructure)
- **IP:** 192.168.179.1 (legacy — migration pending)
- **CPU:** 4 cores
- **RAM:** 15.5 GB
- **Local Storage:** ~38 GB (local-lvm), ~54 GB (local-lvm thin pool)
- **NAS Storage:** ds1813-nfs-volume-1/2/3 (shared)
- **Uptime:** ~10.1 days

### desktop-01 — Gaming PC
- **Role:** Tertiary / dev host
- **IP:** 192.168.179.2 (legacy)
- **Status:** Online
- **Notes:** 3 VMs (VMID 1001–1003) assigned. Purpose to be confirmed.

---

## Storage

### DS1813-01 — Synology DS1813+
- **IP:** 192.168.179.11 (legacy — migration pending)
- **Role:** Shared NAS storage for all Proxmox nodes via NFS
- **Volumes:**

| Volume | Mount | Total | Used | Use |
|---|---|---|---|---|
| Volume 1 | ds1813-nfs-volume-1 | 1.8 TB | 258 GB | General |
| Volume 2 | ds1813-nfs-volume-2 | 3.6 TB | 66 GB | General |
| Volume 3 | ds1813-nfs-volume-3 | 5.4 TB | 3.1 TB | Media / large data |

---

## Network Equipment

### Cloud Gateway Ultra — UniFi UDRULT
- **IP:** 192.168.178.1
- **Role:** Core router, VLAN management, firewall, UniFi controller
- **Firmware:** 5.0.16 (UniFi OS / Site Manager)
- **Uptime:** 9.4 days

### US 24 PoE 250W — UniFi US24P250
- **IP:** 10.0.53.142
- **Role:** Core 24-port PoE switch
- **Firmware:** 7.2.123
- **Uptime:** 32 days

### Access Points — UniFi U7LR (×3)

| Name | IP | Location | Uptime |
|---|---|---|---|
| AC LR (Lounge) | 10.0.14.89 | Lounge | 13.8d |
| AC LR (Kitchen) | 10.0.93.133 | Kitchen | 32.0d |
| AC LR (Master Bedroom) | 10.0.161.161 | Master Bedroom | 32.0d |

### Switches — UniFi USW Flex Mini (×2)

| Name | IP | Location | Status |
|---|---|---|---|
| USW Flex Mini (Lounge) | 10.0.217.66 | Lounge | **Offline** |
| USW Flex Mini (Master Bedroom) | 10.0.111.213 | Master Bedroom | **Offline** |

---

## Other Devices

### Zigbee Gateway
- **Hostname:** tube-zb-gw-efr32-c762b0
- **IP:** 192.168.179.222 (legacy)
- **Role:** Zigbee coordinator (likely connected to Home Assistant)
