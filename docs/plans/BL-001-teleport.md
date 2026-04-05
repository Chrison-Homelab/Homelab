# BL-001: Teleport Investigation & Deployment Plan

## Context

The homelab currently has no authentication layer for SSH access or web UIs. Traefik (LXC 2007) is running but has no auth backend. Keycloak (LXC 2006) was provisioned for SSO but is stopped and never configured. There are 20+ LXC containers and 2 active Proxmox nodes with no centralized access control — SSH is via direct key auth, IPs are mid-migration (192.168.179.x → 10.x), and there's no audit trail.

**Decision: Deploy Teleport.** It uniquely covers SSH certificate management + session recording (not provided by Authentik, Keycloak, or Cloudflare Access), while also handling web UI app access SSO. Keycloak is fully redundant and will be decommissioned.

---

## What Teleport Replaces / Adds

| | Before | After |
|---|---|---|
| SSH auth | Manual authorized_keys on each node/LXC | Certificate-based via Teleport CA |
| SSH audit | None | Full session recording + replay |
| Node addressing | IP-based (breaking during migration) | Label-based (`tsh ssh sonarr`) |
| Web UI SSO | Nothing (Keycloak stopped, never used) | Teleport App Access |
| Keycloak | Stopped, 2 GB RAM / 2 vCPU wasted | Decommissioned (freed) |
| Traefik | Internal routing only | Unchanged — coexists with Teleport |
| Cloudflare tunnel | Transport only, no auth | Unchanged — Teleport handles auth layer |

---

## Architecture

```
Internet
  └── Cloudflare Tunnel (cloudflared-01)
        └── teleport-01 proxy (10.10.x.x, nuc-01)
              ├── SSH Proxy → hpe-01 agent → LXC containers (hpe-01)
              ├── SSH Proxy → nuc-01 agent → LXC containers (nuc-01)
              ├── App Access → sonarr, radarr, plex, prowlarr, bazarr...
              ├── App Access → Proxmox UI (hpe-01:8006, nuc-01:8006)
              └── App Access → Home Assistant
```

---

## New Resource: teleport-01 LXC

| Field | Value |
|---|---|
| Node | nuc-01 |
| VLAN | Homelab (10.10.0.0/16) |
| vCPU | 1 |
| RAM | 1 GB |
| Disk | 8 GB (local-lvm) |
| OS | Debian 12 |
| VMID | next available (after 9903) |

Session recordings optionally stored on ds1813-nfs-volume-1 (low usage, 258 GB used of 1.8 TB).

---

## Decisions

- **Remote access**: Route through existing Cloudflare tunnel (cloudflared-01). No new ports opened. `teleport.yourdomain.com` added to cloudflared config pointing at teleport-01's internal address.
- **SSH scope**: Teleport agent inside each LXC container. Full per-container audit granularity. Scripted via `pve-scripts-local`.

---

## Implementation Phases

### Phase 1 — Core Deployment
1. Create `teleport-01` LXC on nuc-01 (Homelab VLAN, 10.10.x.x)
2. Install Teleport v17+ on teleport-01
3. Configure `teleport.yaml`: auth + proxy service, cluster name, TLS via Cloudflare
4. Add `teleport.yourdomain.com` route to cloudflared-01 config
5. Bootstrap admin user (`tctl users add`)
6. Verify web UI accessible at `teleport.yourdomain.com`

### Phase 2 — SSH: Proxmox Nodes
7. Install Teleport SSH agent on hpe-01, register with teleport-01
8. Install Teleport SSH agent on nuc-01, register with teleport-01
9. Verify `tsh ssh root@hpe-01` and `tsh ssh root@nuc-01` with session recording

### Phase 3 — SSH: LXC Containers
10. Write Teleport agent install script in `pve-scripts-local`
11. Deploy agent to infrastructure containers: homeassistant, traefik, cloudflared-01, proxmox-datacenter-manager
12. Deploy agent to arr-stack containers: sonarr, radarr, prowlarr, bazarr, qbittorrent, plex, seerr, tautulli, flaresolverr
13. Deploy agent to app containers: mealie, kitchenowl, audiobookshelf, shelfmark, romm
14. Verify all containers visible via `tsh ls`

### Phase 4 — App Access (Web UIs)
15. Register Proxmox UI (hpe-01:8006, nuc-01:8006)
16. Register Home Assistant
17. Register arr-stack UIs (Sonarr, Radarr, Prowlarr, Bazarr, qBittorrent, Plex, Seerr, Tautulli)
18. Register Proxmox Datacenter Manager, Traefik dashboard
19. Verify SSO login flow for each app

### Phase 5 — Cleanup
20. Decommission Keycloak LXC (2006) — free 2 GB RAM / 2 vCPU on nuc-01
21. Update BL-005 → Won't Do (replaced by Teleport)
22. Update Services.md with teleport-01, mark Keycloak decommissioned

---

## Files to Create / Modify

- `docs/Backlog.md` — track progress against phases above
- `docs/Services.md` — add teleport-01, mark Keycloak decommissioned
- `src/Proxmox/install-teleport-agent.sh` — agent install script for LXC containers
- `src/Proxmox/install-teleport-agent.ps1` — PowerShell equivalent
- `infra/teleport/teleport.yaml` — Teleport server config template

---

## Verification

- `tsh status` shows authenticated session
- `tsh ls` lists all Proxmox nodes and LXC containers
- `tsh ssh root@sonarr` connects with session recording active
- Teleport web UI shows session recordings for test SSH sessions
- App access URLs respond with Teleport login before proxying to app
- `tctl users ls` shows single admin user
- Keycloak LXC stopped and removed, nuc-01 RAM freed
