# Devices

Physical inventory for all homelab devices. Auto-documented from the Proxmox +
UniFi MCP servers (read-only discovery, BL-009).
**Last updated:** 2026-05-29

---

## Proxmox Hosts

All three nodes run **Proxmox VE 9.2.2** (kernel `7.0.2-6-pve`), each with **16 GB
RAM**. Synology NFS is mounted at the host level on every node (see [NAS](#nas)).

### hpe-01 — HP EliteDesk 800 G2 DM
- **Role:** Primary workload host (most *arr LXCs, Home Assistant, Plex)
- **CPU:** Intel i5-6500T @ 2.5 GHz — 4 cores / 4 threads
- **RAM:** 16 GB · **Boot:** UEFI
- **Local storage:** `local-lvm` ~343 GB (LVM-thin), `local` ~100 GB (dir)
- **Mgmt IP:** 192.168.179.3 (legacy — migration pending, BL-002)

### nuc-01 — Intel NUC D34010WYK
- **Role:** Secondary host (Traefik, Teleport, PDM, *arr overflow)
- **CPU:** Intel i3-4010U @ 1.7 GHz — 2 cores / 4 threads
- **RAM:** 16 GB · **Boot:** UEFI
- **Local storage:** `local-lvm` ~58 GB (LVM-thin), `local` ~41 GB (dir)
- **Mgmt IP:** 192.168.179.1 (legacy — migration pending)

### desktop-01 — Gaming PC
- **Role:** Tertiary / dev + gaming VMs + AI workloads
- **CPU:** AMD Ryzen 5 3600 — 6 cores / 12 threads
- **RAM:** 16 GB · **Boot:** Legacy BIOS
- **Local storage:** `local-lvm` ~1.8 TB (LVM-thin), `local` ~100 GB (dir)
- **Mgmt IP:** 192.168.179.2 (legacy)
- **VMs (1001–1003, all stopped):** `Plex-VM`, `gaming-vm-01`, `gaming-vm-02`
  *(previously "purpose unknown" — identified via discovery, resolves BL-003)*

---

## NAS

### DS1813-01 — Synology DS1813
- **Role:** Shared NAS storage for all Proxmox nodes via NFS
- **IP:** 192.168.179.11 (legacy — migration pending)

NFS volumes (mounted on every node, `shared`):

| Volume | Total | Used | Content |
|---|---|---|---|
| `ds1813-nfs-volume-1` | ~1.9 TB | ~276 GB | images, rootdir, backup, iso, vztmpl, snippets, import |
| `ds1813-nfs-volume-2` | ~3.8 TB | ~71 GB | images, rootdir, backup, iso, vztmpl, snippets, import |
| `ds1813-nfs-volume-3` | ~5.7 TB | ~3.9 TB | images, rootdir, backup, iso, vztmpl, snippets, import (media) |

---

## Network Equipment

Managed by the UniFi Cloud Gateway (gateway MAC `1c:6a:1b:43:62:57`). Switches and
APs live on the **Network Devices** VLAN (10.0.0.0/16).

### Cloud Gateway — UniFi
- **Role:** Core router, VLAN management, firewall, UniFi controller
- **IP:** 192.168.178.1
- *(Not returned under the `ugw` device type during discovery — carried from prior
  inventory; re-verify model/firmware when convenient.)*

### Switch — UniFi US 24 PoE 250W (US24P250)
- **IP:** 10.0.53.142 · **Firmware:** 7.4.1 · 🟢 Online (~34d uptime)

### Access Points — UniFi U7LR (AC LR) ×3 · firmware 6.8.2 · 🟢 Online

| Name | IP |
|---|---|
| AC LR (Lounge) | 10.0.14.89 |
| AC LR (Kitchen) | 10.0.93.133 |
| AC LR (Master Bedroom) | 10.0.161.161 |

### Switches — UniFi USW Flex Mini (USMINI) ×2 · firmware 2.1.6

| Name | IP | Status |
|---|---|---|
| USW Flex Mini (Lounge) | 10.0.217.66 | 🔴 **Offline** |
| USW Flex Mini (Master Bedroom) | 10.0.111.213 | 🔴 **Offline** |

> Both Flex Minis report offline — see BL-006.

---

## Other Devices

### Zigbee Gateway
- **Hostname:** `tube-zb-gw-efr32-c762b0` · **IP:** 192.168.179.222 (legacy)
- **Role:** Zigbee coordinator (feeds Home Assistant)

---

*Service inventory (LXCs/VMs) → [Services.md](Services.md). Network layout →
[Network.md](Network.md).*
