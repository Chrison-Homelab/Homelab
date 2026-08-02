# Homelab Services

Auto-documented from the Proxmox + UniFi MCP servers (read-only discovery, BL-009).
**Last updated:** 2026-05-29

Structured for both human reference and AI-assisted optimization. Key fields:
`Network` (legacy vs migrated), `vCPU/RAM` (right-sizing), `Status`.
`Net` legend: ✅ Homelab `10.10.x` · ⚠️ legacy `192.168.17x` · `—` no DHCP lease seen.

---

## Proxmox Cluster Nodes

| Node | Hardware | vCPU | RAM | Local disk | Status | Mgmt IP |
|---|---|---|---|---|---|---|
| hpe-01 | HP EliteDesk 800 G2 DM (i5-6500T) | 4 | 16 GB | ~320 GB | 🟢 Online | `10.0.0.13` ✅ (`hpe-01.homelab.chrison.internal`) |
| nuc-01 | Intel NUC D34010WYK (i3-4010U) | 2/4 | 16 GB | ~54 GB | 🟢 Online | `10.0.0.11` ✅ (`nuc-01.homelab.chrison.internal`) |
| desktop-01 | Gaming PC (Ryzen 5 3600) | 6/12 | 16 GB | ~1.71 TB | 🟢 Online | `10.0.0.12` ✅ (`desktop-01.homelab.chrison.internal`) |

**Shared NAS storage (NFS from DS1813-01):** `ds1813-nfs-volume-1` ~1.9 TB ·
`-2` ~3.8 TB · `-3` ~5.7 TB (media, ~3.9 TB used). Mounted on all nodes.

---

## Infrastructure Services

| Name | VMID | Type | Node | Status | IP | Net | vCPU | RAM | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| cloudflared-01 | 2001 | LXC | hpe-01 | 🟢 Running | 10.10.133.209 | ✅ | 1 | 512 MB | Cloudflare tunnel (remote access) |
| traefik | 2007 | LXC | nuc-01 | 🟢 Running | — | — | 1 | 512 MB | Reverse proxy / ingress |
| teleport | 9904 | LXC | nuc-01 | 🟢 Running | — | — | 1 | 1 GB | Zero-trust access (SSH + app SSO) |
| proxmox-datacenter-manager | 2002 | LXC | nuc-01 | 🟢 Running | 10.10.208.155 | ✅ | 2 | 2 GB | Proxmox Datacenter Manager UI |
| pve-scripts-local | 2003 | LXC | nuc-01 | 🟢 Running | — | — | 2 | 4 GB | Local Proxmox helper scripts |
| github-runner | 2005 | LXC | desktop-01 | 🔴 Stopped | — | ✅ | 2 | 2 GB | **RETIRED 2026-08-02** (#337) — was a runner for `ChrisonSimtian/ERP.Satisfactory`, not this org; deregistered |

> **Teleport (9904) is deployed and running** — resolves BL-001. No `keycloak`
> container exists (it was replaced by Teleport — closes BL-005).

## Smart Home

| Name | VMID | Type | Node | Status | IP | Net | vCPU | RAM | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| homeassistant | 2000 | VM (QEMU) | hpe-01 | 🟢 Running | 192.168.179.102 | ⚠️ | 2 | 2 GB | Smart-home controller (VLAN 1010 + 1040) |

## Media Stack (*arr)

Tagged `arr-stack`. Target VLAN: Homelab `10.10.0.0/16`.

| Name | VMID | Type | Node | Status | IP | Net | vCPU | RAM | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| prowlarr | 5002 | LXC | hpe-01 | 🟢 Running | 192.168.179.152 | ⚠️ | 1 | 1 GB | Indexer manager |
| sonarr | 5003 | LXC | hpe-01 | 🟢 Running | 192.168.179.153 | ⚠️ | 1 | 1 GB | TV automation |
| radarr | 5004 | LXC | hpe-01 | 🟢 Running | 192.168.179.154 | ⚠️ | 1 | 1 GB | Movie automation |
| bazarr | 5006 | LXC | nuc-01 | 🟢 Running | 192.168.179.156 | ⚠️→✅ | 1 | 1 GB | Subtitles (migrating — new 1010 lease seen) |
| flaresolverr | 5005 | LXC | nuc-01 | 🟢 Running | — | — | 1 | 2 GB | Captcha bypass for indexers |
| qbittorrent | 5007 | LXC | hpe-01 | 🔴 Stopped | — | — | 2 | 2 GB | Download client |
| plex | 5008 | LXC | hpe-01 | 🟢 Running | 10.10.200.98 | ✅ | 4 | 2 GB | Media server |
| tautulli | 5009 | LXC | nuc-01 | 🟢 Running | — | ✅ | 2 | 1 GB | Plex analytics (VLAN 1010) |
| seerr | 5011 | LXC | hpe-01 | 🟢 Running | 10.10.48.42 | ✅ | 4 | 4 GB | Media request manager |
| tracearr | 5013 | LXC | hpe-01 | 🟢 Running | — | — | 2 | 2 GB | *arr-stack monitoring |

## Media Libraries

| Name | VMID | Type | Node | Status | IP | Net | vCPU | RAM | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| audiobookshelf | 5014 | LXC | hpe-01 | 🟢 Running | — | ✅ | 2 | 2 GB | Audiobook / podcast server |
| shelfmark | 5015 | LXC | hpe-01 | 🟢 Running | 10.10.52.82 | ✅ | 2 | 2 GB | Ebook management |
| romm | 5012 | LXC | hpe-01 | 🟢 Running | — | — | 2 | 4 GB | ROM / emulation library |

## Apps & AI

| Name | VMID | Type | Node | Status | IP | Net | vCPU | RAM | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| searxng | 2004 | LXC | desktop-01 | 🟢 Running | 10.10.137.178 | ✅ | 2 | 2 GB | Meta search engine |
| openwebui | 9905 | LXC | desktop-01 | 🟢 Running | 10.10.97.77 | ✅ | 4 | 8 GB | LLM web UI |
| erp-for-factory-games | 2008 | LXC | desktop-01 | 🟢 Running | 10.10.107.175 | ✅ | 2 | 4 GB | ERP for Factory Games stack (Cloudflare tunnel) |
| obsidian-livesync | 9906 | LXC | nuc-01 | 🟢 Running | — | ⚠️ | 1 | 512 MB | Obsidian sync (CouchDB) |
| cookbook | 9966 | LXC | nuc-01 | 🟢 Running | 10.10.83.46 | ✅ | 1 | 512 MB | Recipe manager |
| asp-dev | 2006 | LXC | desktop-01 | 🔴 Stopped | — | — | 2 | 3 GB | Dev sandbox |

## Gaming / Other VMs (desktop-01, all stopped)

| Name | VMID | Type | vCPU | RAM | Purpose |
|---|---|---|---|---|---|
| Plex-VM | 1001 | VM | 2 | 4 GB | (legacy Plex VM — superseded by `plex` LXC 5008?) |
| gaming-vm-01 | 1002 | VM | 6 | 12 GB | Gaming VM |
| gaming-vm-02 | 1003 | VM | 6 | 12 GB | Gaming VM |

---

## Migration Status (live, 2026-05-29)

Legacy `192.168.17x` → Homelab `10.10.0.0/16`.

**✅ Migrated:** cloudflared-01, proxmox-datacenter-manager, searxng, openwebui,
erp-for-factory-games, seerr, shelfmark, cookbook, tautulli, audiobookshelf.

**⚠️ Still on legacy (priority):**

| Item | Current IP | Priority |
|---|---|---|
| ~~Proxmox nodes~~ | **migrated 2026-08-02** → `10.0.0.13 / .11 / .12` (VLAN 1000) | ✅ done |
| ~~DS1813-01 (NAS)~~ | **migrated 2026-08-02** → `10.0.0.10` (VLAN 1000) | ✅ done |
| homeassistant | 192.168.179.102 | High |
| prowlarr / sonarr / radarr | .152 / .153 / .154 | Medium |
| bazarr | 192.168.179.156 (migrating) | Medium |
| ~~plex (dual-homed)~~ | **legacy NIC dropped 2026-08-02** (#344) → `10.10.200.98` only | ✅ done |
| ~~github-runner~~ | **retired 2026-08-02** (#337) — CT 2005 stopped, runner deregistered | ✅ done |
| obsidian-livesync | (legacy lease) | Low |
| Zigbee GW (`tube-zb-gw…`) | 192.168.179.222 | Low |

> **WiFi is done.** `Blackbox` now lands clients on **Consumer (VLAN 1020)** and
> `Blackbox_IOT` on **IOT (VLAN 1040)** — neither touches the legacy "Old Network"
> any more. That was the largest single blocker for #37; what remains is the
> wired guest list above.

---

## Personal & IoT Clients (selected, live)

| Device | IP | Network |
|---|---|---|
| MacBook Chris | 192.168.178.85 | ⚠️ Old |
| MacBookAir Yuhan | 192.168.179.4 | ⚠️ Old |
| iPhone Chris / Yuhan | 192.168.179.32 / .145 | ⚠️ Old |
| Apple TV Bedroom | 192.168.178.71 | ⚠️ Old |
| Alexa Lounge / Bedroom | 192.168.178.200 / .145 | ⚠️ Old |
| Samsung-Washer | 10.40.84.50 | ✅ IoT (1040) |
| Meross Smart Plug | 10.40.170.241 | ✅ IoT (1040) |
| Yeelink lamp | 10.40.18.24 | ✅ IoT (1040) |

Personal devices (MacBooks, iPhones, Apple TVs, Alexas) are still on the legacy
network — they should move to Consumer `10.20.0.0/16` (VLAN 1020). IoT devices are
correctly on VLAN 1040.

---

## Known Issues / Open TODOs

- [ ] **Legacy network migration** (BL-002) — nodes, NAS, Home Assistant, core
  *arr LXCs, and all personal devices still on `192.168.17x`. Blocked partly by
  the `Blackbox` SSID mapping to Old Network.
- [ ] **2× USW Flex Mini offline** (BL-006) — Lounge + Master Bedroom.
- [ ] **plex dual-homed** — holds both a legacy and a Homelab lease; finish its cutover.
- [ ] **Stopped containers to review:** `qbittorrent` (5007), `asp-dev` (2006);
  VMs `Plex-VM`/`gaming-vm-01/02` (1001–1003).
- [x] ~~desktop-01 VMs unknown~~ → identified (Plex-VM, gaming-vm-01/02).
- [x] ~~Keycloak / SSO~~ → replaced by Teleport (9904), running.
