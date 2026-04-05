# Homelab Services

Auto-documented from Proxmox MCP + UniFi MCP.
**Last updated:** 2026-04-05

This document is structured for both human reference and AI-assisted optimization.
Key fields for optimization: `IP Status` (legacy vs migrated), `vCPU/RAM` (for right-sizing), `Tags` (for grouping/placement decisions).

---

## Proxmox Cluster Nodes

| Node | Hostname | Role | vCPU | RAM | Local Disk | Status | IP (Legacy) |
|---|---|---|---|---|---|---|---|
| hpe-01 | HP EliteDesk 800 G2 DM | Primary workload host | 4 | 15.5 GB | ~94 GB | Online | 192.168.179.3 |
| nuc-01 | Intel NUC D34010WYK | Secondary workload host | 4 | 15.5 GB | ~38 GB | Online | 192.168.179.1 |
| desktop-01 | Gaming PC | Tertiary / dev host | — | — | — | Online | 192.168.179.2 |

**Shared NAS Storage (NFS from DS1813-01):**

| Volume | Size | Used | Purpose |
|---|---|---|---|
| ds1813-nfs-volume-1 | 1.8 TB | 258 GB | General |
| ds1813-nfs-volume-2 | 3.6 TB | 66 GB | General |
| ds1813-nfs-volume-3 | 5.4 TB | 3.1 TB | Media / large data |

---

## Infrastructure Services

| Name | VMID | Type | Node | Status | IP | VLAN | vCPU | RAM | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| homeassistant | 2000 | VM (QEMU) | hpe-01 | Running | 192.168.179.102 ⚠️ | 1010 + 1040 | 2 | 2 GB | Smart home controller |
| cloudflared-01 | 2001 | LXC | hpe-01 | Running | 10.10.133.209 ✅ | Homelab | 1 | 512 MB | Cloudflare tunnel (remote access) |
| proxmox-datacenter-manager | 2002 | LXC | nuc-01 | Running | 10.10.208.155 ✅ | Homelab | 2 | 2 GB | Proxmox Datacenter Manager UI |
| pve-scripts-local | 2003 | LXC | nuc-01 | Running | — | Homelab | 2 | 4 GB | Local Proxmox helper scripts |
| traefik | 2007 | LXC | nuc-01 | Running | — | 1010 | 1 | 512 MB | Reverse proxy / ingress |
| keycloak | 2006 | LXC | nuc-01 | **Stopped** | — | — | 2 | 2 GB | SSO / identity provider (planned) |
| vpn-china | 2005 | LXC | nuc-01 | **Stopped** | — | — | 1 | 512 MB | VPN exit node (China) |

---

## Media Stack (Arr)

All services tagged `arr-stack`. Target VLAN: `10.10.0.0/16` (Homelab). Many still on legacy `192.168.179.x`.

| Name | VMID | Type | Node | Status | IP | IP Status | vCPU | RAM | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| plex | 5008 | LXC | hpe-01 | Running | 192.168.179.62 / 10.10.200.98 | ⚠️ Dual | 2 | 2 GB | Media server |
| sonarr | 5003 | LXC | hpe-01 | Running | 192.168.179.153 | ⚠️ Legacy | 1 | 1 GB | TV show automation |
| radarr | 5004 | LXC | hpe-01 | Running | 192.168.179.154 | ⚠️ Legacy | 1 | 1 GB | Movie automation |
| prowlarr | 5002 | LXC | hpe-01 | Running | 192.168.179.152 | ⚠️ Legacy | 1 | 1 GB | Indexer manager |
| bazarr | 5006 | LXC | nuc-01 | Running | 192.168.179.156 | ⚠️ Legacy | 1 | 1 GB | Subtitle automation |
| qbittorrent | 5007 | LXC | hpe-01 | Running | 192.168.179.157 | ⚠️ Legacy | 2 | 2 GB | Download client |
| flaresolverr | 5005 | LXC | nuc-01 | Running | — | — | 1 | 2 GB | Captcha bypass for indexers |
| tautulli | 5009 | LXC | nuc-01 | Running | — | — | 2 | 1 GB | Plex analytics |
| seerr | 5011 | LXC | hpe-01 | Running | 10.10.48.42 ✅ | ✅ Migrated | 4 | 4 GB | Media request manager |
| tracearr | 5013 | LXC | hpe-01 | Running | — | — | 2 | 2 GB | Arr stack monitoring |
| audiobookshelf | 5014 | LXC | hpe-01 | Running | 10.10.162.226 ✅ | ✅ Migrated | 2 | 2 GB | Audiobook / podcast server |
| shelfmark | 5015 | LXC | hpe-01 | Running | 10.10.52.82 ✅ | ✅ Migrated | 2 | 2 GB | Ebook management |
| romm | 5012 | LXC | hpe-01 | Running | — | — | 2 | 4 GB | ROM / emulation library |
| jellyseerr | 5001 | LXC | hpe-01 | **Stopped** | — | — | 1 | 4 GB | Media requests (replaced by seerr) |
| qbittorrent-clone | 5010 | LXC | nuc-01 | **Stopped** | — | — | 2 | 2 GB | Clone / unused |

---

## Apps & Utilities

| Name | VMID | Type | Node | Status | IP | VLAN | vCPU | RAM | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| mealie | 2004 | LXC | hpe-01 | Running | 10.10.16.239 ✅ | 1010 | 2 | 3 GB | Recipe manager |
| kitchenowl | 9903 | LXC | hpe-01 | Running | 10.10.174.9 ✅ | 1010 | 1 | 2 GB | Grocery / meal planner |
| finance | 9902 | LXC | nuc-01 | **Stopped** | — | 1010 | 2 | 2 GB | Personal finance app (planned) |

---

## Migration Status Summary

Services still on the legacy `192.168.179.x` / `192.168.178.x` network that need migrating to `10.10.0.0/16`:

| Service | Current IP | Priority |
|---|---|---|
| homeassistant | 192.168.179.102 | High (core infrastructure) |
| Proxmox hpe-01 | 192.168.179.3 | High (node itself) |
| Proxmox nuc-01 | 192.168.179.1 | High (node itself) |
| Proxmox desktop-01 | 192.168.179.2 | Low (offline) |
| DS1813-01 (NAS) | 192.168.179.11 | High (shared storage) |
| plex | 192.168.179.62 | Medium |
| sonarr | 192.168.179.153 | Medium |
| radarr | 192.168.179.154 | Medium |
| prowlarr | 192.168.179.152 | Medium |
| bazarr | 192.168.179.156 | Medium |
| qbittorrent | 192.168.179.157 | Medium |

---

## UniFi Network Devices

| Device | Model | IP | VLAN | Status | Uptime | Firmware |
|---|---|---|---|---|---|---|
| Cloud Gateway Ultra | UDRULT | 192.168.178.1 | Network Devices | Online | 9.4d | 5.0.16 |
| US 24 PoE 250W | US24P250 | 10.0.53.142 | Network Devices | Online | 32.0d | 7.2.123 |
| AC LR (Lounge) | U7LR | 10.0.14.89 | Network Devices | Online | 13.8d | 6.8.2 |
| AC LR (Kitchen) | U7LR | 10.0.93.133 | Network Devices | Online | 32.0d | 6.8.2 |
| AC LR (Master Bedroom) | U7LR | 10.0.161.161 | Network Devices | Online | 32.0d | 6.8.2 |
| USW Flex Mini (Lounge) | USMINI | 10.0.217.66 | Network Devices | **Offline** | — | 2.1.6 |
| USW Flex Mini (Master Bedroom) | USMINI | 10.0.111.213 | Network Devices | **Offline** | — | 2.1.6 |

---

## Wireless Clients

| Name | IP | VLAN | Notes |
|---|---|---|---|
| MacBook Chris | 192.168.178.85 | Legacy | Primary dev machine |
| MacBookAir Yuhan | 192.168.179.4 | Legacy | |
| iPhone Chris | 192.168.179.32 | Legacy | |
| iPhohe Yuhan | 192.168.179.145 | Legacy | |
| iWatch Chris | 192.168.179.159 | Legacy | |
| Watch | 192.168.179.204 | Legacy | |
| iPad | 192.168.178.48 | Legacy | |
| Apple TV Bedroom | 192.168.178.71 | Legacy/Consumer | |
| Alexa Lounge | 192.168.178.200 | Legacy/Consumer | |
| Alexa Bedroom | 192.168.178.145 | Legacy/Consumer | |
| Karl Babymonitor | 192.168.178.78 | Legacy/Consumer | |
| Fan Karl | 192.168.178.130 | Legacy/Consumer | |
| Fan Bedroom | 192.168.179.224 | Legacy/Consumer | |
| Samsung-Washer | 10.40.84.50 | IoT ✅ | |
| Meross Smart Plug | 10.40.170.241 | IoT ✅ | |
| d8:c8:0c:b0:9e:28 | 10.40.2.131 | IoT ✅ | Unknown device |

---

## Known Issues / Open TODOs

- [ ] **desktop-01 VMs unknown** — 3 VMs (VMID 1001–1003) assigned, purpose to be confirmed
- [ ] **2x USW Flex Mini offline** — Lounge and Master Bedroom switches need investigation
- [ ] **Legacy network migration** — Proxmox nodes, NAS, and most arr-stack still on 192.168.179.x
- [ ] **keycloak stopped** — SSO not yet deployed; traefik has no auth backend
- [ ] **jellyseerr stopped** — appears superseded by seerr; candidate for removal
- [ ] **qbittorrent-clone stopped** — likely unused; candidate for removal
- [ ] **Plex has dual IPs** — indicates incomplete VLAN migration
- [ ] **Consumer/personal devices still on legacy network** — MacBooks, iPhones, Apple TV, Alexas should move to 10.20.x.x
