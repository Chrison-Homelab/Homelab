# Homelab Backlog

Structured backlog for homelab improvements, investigations, and migrations.
Maintained collaboratively with Claude Code — items are sized and sequenced for incremental delivery.

**Statuses:** `Idea` → `Planned` → `In Progress` → `Done`

---

## Active

### BL-001 — Investigate & Deploy Teleport (Authentication)
**Status:** Planned
**Plan:** [docs/plans/BL-001-teleport.md](plans/BL-001-teleport.md)
**Priority:** High
**Tags:** `security` `auth` `infrastructure`

Investigate whether Teleport provides value for this homelab as an authentication and access layer. If beneficial, deploy it and integrate individual applications.

**Investigation questions:**
- Does Teleport replace or complement Traefik + Keycloak?
- What does Teleport provide that Keycloak alone does not?
- Is Teleport Community Edition sufficient for a single-person homelab?
- How does it handle SSH access to Proxmox nodes and LXCs?
- Can it gate access to web UIs (Proxmox, Plex, arr-stack)?
- What is the operational overhead vs benefit?

**Investigation outcome:** Deploy Teleport. Replaces Keycloak entirely. Adds SSH certificate auth + session recording across all nodes and LXCs. Routed via existing Cloudflare tunnel (no new ports). Agent deployed inside each LXC for per-container auditing.

**Architecture:**
- New LXC `teleport-01` on nuc-01, Homelab VLAN (10.10.x.x), 1 vCPU / 1 GB RAM / 8 GB disk
- Cloudflare tunnel routes `teleport.yourdomain.com` → teleport-01
- Teleport agent on hpe-01, nuc-01 (SSH + App Access)
- Teleport agent inside each LXC (SSH, per-container recording)

**Phase 1 — Core Deployment**
- [ ] Create `teleport-01` LXC on nuc-01 (Homelab VLAN, 10.10.x.x)
- [ ] Install Teleport v17+ and configure `teleport.yaml` (auth + proxy + app service)
- [ ] Add `teleport.yourdomain.com` route to cloudflared-01 config
- [ ] Bootstrap admin user, verify web UI accessible

**Phase 2 — SSH: Proxmox Nodes**
- [ ] Install Teleport SSH agent on hpe-01, register with teleport-01
- [ ] Install Teleport SSH agent on nuc-01, register with teleport-01
- [ ] Verify `tsh ssh root@hpe-01` and `tsh ssh root@nuc-01` with session recording

**Phase 3 — SSH: LXC Containers**
- [ ] Write Teleport agent install script in `pve-scripts-local`
- [ ] Deploy agent to infrastructure containers (homeassistant, traefik, cloudflared-01, proxmox-datacenter-manager)
- [ ] Deploy agent to arr-stack containers (sonarr, radarr, prowlarr, bazarr, qbittorrent, plex, seerr, tautulli, bazarr, flaresolverr)
- [ ] Deploy agent to app containers (mealie, kitchenowl, audiobookshelf, shelfmark, romm)
- [ ] Verify all containers visible via `tsh ls`

**Phase 4 — App Access (Web UIs)**
- [ ] Register Proxmox UI (hpe-01:8006, nuc-01:8006)
- [ ] Register Home Assistant
- [ ] Register arr-stack UIs (Sonarr, Radarr, Prowlarr, Bazarr, qBittorrent, Plex, Seerr, Tautulli)
- [ ] Register Proxmox Datacenter Manager, Traefik dashboard
- [ ] Verify SSO login flow for each app

**Phase 5 — Cleanup**
- [ ] Decommission Keycloak LXC (2006) — free 2 GB RAM / 2 vCPU on nuc-01
- [ ] Update Services.md with teleport-01, mark Keycloak decommissioned
- [ ] Update BL-005 (Keycloak) → Won't Do (replaced by Teleport)

---

## Backlog (Not Started)

### BL-002 — Legacy Network Migration (192.168.179.x → 10.x)
**Status:** Idea
**Priority:** High
**Tags:** `networking` `migration`

Move all devices and services off the legacy `192.168.179.x` subnet onto the proper VLAN-segmented `10.x.x.x` architecture. See migration status table in `Services.md`.

Key items:
- Proxmox nodes (hpe-01, nuc-01, desktop-01) → `10.0.x.x` (Network Devices VLAN)
- Synology DS1813-01 → `10.0.x.x`
- Arr-stack LXCs → `10.10.x.x` (Homelab VLAN)
- Personal devices (MacBooks, iPhones) → `10.20.x.x` (Consumer VLAN)
- Consumer devices (Alexas, Apple TV) → `10.20.x.x`

---

### BL-003 — Clarify & Document desktop-01 VMs
**Status:** Idea
**Priority:** Low
**Tags:** `documentation`

3 VMs (VMID 1001–1003) on desktop-01 have unknown purpose. Identify, document, or decommission.

---

### BL-004 — Clean Up Stopped/Unused Containers
**Status:** Idea
**Priority:** Low
**Tags:** `cleanup`

Review and remove containers that are stopped and appear unused:
- `jellyseerr` (5001) — superseded by `seerr`
- `qbittorrent-clone` (5010) — likely unused clone
- `vpn-china` (2005) — assess if still needed

---

### BL-005 — Deploy Keycloak (or Replace with Teleport)
**Status:** Idea
**Priority:** Medium
**Tags:** `auth` `security`

Keycloak (LXC 2006) is deployed but stopped. Traefik has no auth backend. Either:
- Start Keycloak and configure forward auth with Traefik, or
- Replace with Teleport (see BL-001)

Blocked on BL-001 decision.

---

### BL-006 — Investigate USW Flex Mini Offline Issues
**Status:** Idea
**Priority:** Medium
**Tags:** `networking` `hardware`

Both USW Flex Mini switches (Lounge + Master Bedroom) are offline. Investigate cause — power, cable, or firmware issue.

---

### BL-007 — Fallout CI/CD for private submodules
**Status:** Idea
**Priority:** Medium
**Tags:** `ci-cd` `fallout` `gitops` `auth`

Wire the hub repo's GitOps pipeline up with [Fallout](https://github.com/ChrisonSimtian/Fallout) (the C#/.NET build system) and solve checking out the private `Homelab.Stacks.*` submodules in GitHub Actions. The submodules are wired in (PR #6) but a default Actions checkout can't read them — needs an auth strategy. Deliberately deferred from the submodule-wiring PR to its own session.

**Investigation questions:**
- What auth does Fallout-generated Actions need to checkout private submodules — PAT, GitHub App token, or per-repo deploy keys?
- Does Fallout generate the submodule checkout step, or is it hand-added to the generated workflow?
- One credential with org-wide read, or scoped per submodule?
- How are secrets surfaced to the runner (Actions secrets vs. Bitwarden, matching the stacks' deploy pattern)?

- [ ] Add a Fallout build project (C#) to the hub repo
- [ ] Generate the `.github/workflows/*.yml` via Fallout
- [ ] Configure private-submodule checkout auth in the generated workflow
- [ ] Verify a clean CI run checks out all five stacks
- [ ] Document the chosen auth approach in the README / `docs/Development.md`

---

## Done

*(nothing yet)*
