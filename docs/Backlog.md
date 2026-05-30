# Homelab Backlog

Structured backlog for homelab improvements, investigations, and migrations.
Maintained collaboratively with Claude Code — items are sized and sequenced for incremental delivery.

**Statuses:** `Idea` → `Planned` → `In Progress` → `Done`

---

## Active

### BL-001 — Investigate & Deploy Teleport (Authentication)
**Status:** ✅ Done — confirmed via discovery 2026-05-29 (`teleport` LXC 9904 running on nuc-01; no `keycloak` container exists)
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

### BL-002 — Legacy Network Migration (192.168.178.1/23 → 10.x)
**Status:** In Progress
**Priority:** High
**Tags:** `networking` `migration`

Move all devices and services off the legacy `192.168.178.1/23` subnet (spans
`.178.x`–`.179.x`) onto the VLAN-segmented `10.x` architecture. Live migration
status is in [Services.md](Services.md) (auto-documented 2026-05-29).

**Discovery finding:** the primary WiFi SSID `Blackbox` still maps to the Old
Network — a hard blocker for moving personal devices. Still legacy: all 3 Proxmox
nodes, DS1813 NAS, Home Assistant, prowlarr/sonarr/radarr/bazarr, github-runner,
plex (dual-homed), and most personal devices.

Key items:
- Proxmox nodes (hpe-01, nuc-01, desktop-01) → `10.0.x.x` (Network Devices VLAN)
- Synology DS1813-01 → `10.0.x.x`
- Arr-stack LXCs → `10.10.x.x` (Homelab VLAN)
- Personal devices (MacBooks, iPhones) → `10.20.x.x` (Consumer VLAN)
- Consumer devices (Alexas, Apple TV) → `10.20.x.x`

---

### BL-003 — Clarify & Document desktop-01 VMs
**Status:** ✅ Done — identified via discovery 2026-05-29
**Priority:** Low
**Tags:** `documentation`

VMs 1001–1003 on desktop-01 identified: `Plex-VM` (1001), `gaming-vm-01` (1002),
`gaming-vm-02` (1003) — all currently stopped. Documented in [Devices.md](Devices.md)
/ [Services.md](Services.md). Decommission decision (esp. `Plex-VM` vs the `plex`
LXC) is a separate cleanup.

---

### BL-004 — Clean Up Stopped/Unused Containers
**Status:** ✅ Done — confirmed via discovery 2026-05-29 (all three removed)
**Priority:** Low
**Tags:** `cleanup`

Original targets are gone: `jellyseerr` (5001), `qbittorrent-clone` (5010), and
`vpn-china` (2005) no longer exist (CTID 2005 is now `github-runner`). New
stopped-container review candidates surfaced by discovery → tracked informally in
[Services.md](Services.md): `qbittorrent` (5007), `asp-dev` (2006), and the
stopped VMs 1001–1003.

---

### BL-005 — Deploy Keycloak (or Replace with Teleport)
**Status:** 🚫 Won't Do — superseded by Teleport (BL-001)
**Priority:** Medium
**Tags:** `auth` `security`

Resolved by BL-001: Teleport was deployed and Keycloak removed. No `keycloak`
container exists anymore (former CTID 2006 is now `asp-dev`).

---

### BL-006 — Investigate USW Flex Mini Offline Issues
**Status:** Idea
**Priority:** Medium
**Tags:** `networking` `hardware`

Both USW Flex Mini switches (Lounge + Master Bedroom) are offline. Investigate cause — power, cable, or firmware issue. **Confirmed still offline via discovery 2026-05-29** (Lounge 10.0.217.66, Master Bedroom 10.0.111.213; both firmware 2.1.6).

---

### BL-007 — Fallout CI/CD + GitHub Environments & Releases
**Status:** In Progress
**Doc:** [docs/CICD.md](CICD.md)
**Priority:** Medium
**Tags:** `ci-cd` `fallout` `gitops` `auth` `environments` `releases`

Wire the hub repo's GitOps pipeline up with [Fallout](https://github.com/ChrisonSimtian/Fallout)
and use **GitHub Environments + Releases** to track homelab state. See
[docs/CICD.md](CICD.md) for the full model.

**Decisions (2026-05-29):**
- **Environments:** per-node (`hpe-01`/`nuc-01`/`desktop-01`) + a `homelab` umbrella.
- **Releases:** on-demand state snapshots (workflow_dispatch) bundling
  `docs/` + `Infrastructure/` + pinned submodule SHAs.
- **Submodule auth:** fine-grained PAT stored as Actions secret `SUBMODULES_PAT`.

- [x] Create the four GitHub Environments
- [x] Add the on-demand `release-state-snapshot` workflow + `docs/CICD.md`
- [x] Run `fallout :setup` to scaffold the build project (`build/`) *(on WIP branch `feat/fallout-ci-wip`)*
- [x] Write the `ValidateShapes` target + `[GitHubActions("ci")]` attribute *(on WIP branch)*
- [ ] **BLOCKED — restore fails.** Fallout's restructure pulled everything past
      `10.3.x` from NuGet.org, so `Fallout.Common`/`fallout.cli` `11.0.18` can't
      restore. Fix is to consume the **experimental Fallout package channel**
      (being stood up in a separate session), then re-pin `build/build.csproj`
      + `.config/dotnet-tools.json` to a version that channel actually serves.
      *(Earlier "rebrand namespace" theory in the WIP-branch note was wrong —
      it's a package-availability problem, not a namespace one.)*
- [ ] Generate `.github/workflows/ci.yml` (blocked on the restore above)
- [ ] Create the fine-grained PAT and store it as `SUBMODULES_PAT` *(Chris)*
- [ ] Verify CI runs `ValidateShapes` green; verify release workflow checks out submodules

---

### BL-008 — C#-native IaC: Define shapes
**Status:** In Progress
**Plan:** [docs/plans/iac-csharp-native.md](plans/iac-csharp-native.md) · **ADR:** [ADR-0001](adr/ADR-0001-iac-tooling.md)
**Priority:** High
**Tags:** `iac` `infrastructure` `csharp` `dogfood`

Phase 1 (Define) of the C#-native IaC initiative. Establish the declarative
**shape** contract under `/Infrastructure` so submodules can declare what they
need provisioned, before any engine code exists.

- [x] ADR-0001 + plan recorded
- [x] `/Infrastructure` scaffold: schema, nodes, reference example
- [ ] Iterate the shape schema as real fields surface
- [ ] Author per-submodule `shape.yaml` (after Phase 2 discovery grounds them)

---

### BL-009 — C#-native IaC: Discover (read-only state import)
**Status:** In Progress — ProxmoxSharp M1–M5 done 2026-05-30 (codegen + discover, published to GitHub Packages); reconcile vs. shapes next
**Plan:** [docs/plans/iac-csharp-native.md](plans/iac-csharp-native.md) · **Codegen plan:** [docs/plans/BL-009-proxmoxsharp-codegen.md](plans/BL-009-proxmoxsharp-codegen.md)
**Priority:** High
**Tags:** `iac` `proxmox` `proxmoxsharp` `discovery` `codegen`

Phase 2. Grab current Proxmox state read-only and reconcile shapes against it.
First real use of [ProxmoxSharp](https://github.com/ChrisonSimtian/ProxmoxSharp)
(vendored at `vendor/ProxmoxSharp`, pinned at M3). **Route A** chosen: generate the
client from Proxmox's `apidoc.js` (version-matched) → OpenAPI → Kiota, on a thin
hand-written runtime. Split into `ProxmoxSharp.Api` (generated, regenerate-on-build,
version = PVE release) + `ProxmoxSharp` (library, SemVer). CI green.

- [x] Stand up a Proxmox MCP (`Samik081/mcp-pve`, read-only tier) + UniFi MCP (`enuno`)
- [x] First read-only sweep of the live cluster + network (via MCP)
- [x] Regenerate `Devices.md` + `Services.md` + `Network.md` from reality
- [x] M0–M1: vendor ProxmoxSharp; confirm `apidoc.js` on-node; scaffold solution (Route A, net10.0)
- [x] M2: `ProxmoxClient` + PVEAPIToken auth + first live read (`GET /nodes`) verified
- [x] M3: schema→OpenAPI→Kiota codegen; live reads of `/version` + the `/nodes` subtree (189 ops); regenerate-on-build + CI
- [x] Widen generated coverage to `/version,/nodes,/cluster,/storage,/access` (338 GET ops)
- [x] M4: `ProxmoxDiscovery.DiscoverAsync()` → structured `ClusterSnapshot` (nodes → LXC/QEMU/storage/network), live-verified
- [x] M5: both packages published to GitHub Packages (`ProxmoxSharp.Api` 9.2.2 + `ProxmoxSharp` 0.1.0) via `v*`-tag workflow
- [ ] Reconcile hand-written shapes vs. discovered state (started in `/Infrastructure/nodes`)
- [ ] M5: package ProxmoxSharp as NuGet the hub consumes

---

### BL-010 — C#-native IaC: Converge (reproducible provisioning)
**Status:** Idea
**Plan:** [docs/plans/iac-csharp-native.md](plans/iac-csharp-native.md)
**Priority:** Medium
**Tags:** `iac` `proxmox` `proxmoxsharp`

Phase 3. Turn on create/update/destroy, gated behind plan/dry-run.

- [ ] ProxmoxSharp write path (LXC + VM lifecycle + config)
- [ ] `plan` — diff desired shapes vs. discovered state, no mutation
- [ ] `apply` — converge, dry-run by default, explicit confirm to mutate
- [ ] Idempotency + reversibility guarantees
- [ ] Repackage engine as a Fallout plugin once v12 `Fallout.Plugin.Sdk` ships

---

### BL-011 — Unifi: discovery + C# client
**Status:** Idea
**Priority:** Medium
**Tags:** `networking` `unifi` `iac` `csharp`

Bring the Unifi-managed network under the same model. Explore via an MCP, then
build a small C# Unifi client so network config is deployable too.

- [ ] Find / stand up a Unifi MCP; explore the current network
- [ ] Document current network state into `docs/Network.md`
- [ ] Build a small C# Unifi API client
- [ ] Define network shapes (VLANs, firewall, ports) under `/Infrastructure`

---

### BL-012 — Homelab dashboard
**Status:** Idea
**Priority:** Low
**Tags:** `dashboard` `docs` `github-pages`

A dashboard for the homelab, likely a GitHub Page linked against an owned domain,
fed by the discovery output (BL-009).

- [ ] Decide host (GitHub Pages) + domain
- [ ] Decide content (inventory, stack status, network map)
- [ ] Wire it to discovery output so it stays current

---

### BL-013 — Provision via community-scripts over SSH
**Status:** In Progress — renderer built + **live smoke test passed** 2026-05-29 (CT 3099 created on hpe-01, verified via MCP, destroyed)
**Plan:** [docs/plans/BL-013-community-scripts-deploy.md](plans/BL-013-community-scripts-deploy.md)
**Priority:** High — the unblocked Proxmox deploy foundation (C#/Fallout path is blocked)
**Tags:** `iac` `proxmox` `community-scripts` `converge`
**Relates to:** [BL-010](#bl-010--c-native-iac-converge-reproducible-provisioning)

Chris already provisions most Proxmox infra with
[community-scripts.org](https://community-scripts.org/) and wants to stick with
it. Confirmed mechanism: **`mode=generated`** bypasses the whiptail menu
(`build.func`: `CHOICE="${mode:-${1:-}}"`) and the exported `var_*` drive a
non-interactive, no-TTY install over SSH —
`mode=generated var_ctid=3000 … bash -c "$(curl -fsSL …/ct/<app>.sh)"`.

Decided split (the create mechanism for **Converge**, BL-010): **community-scripts
create over SSH**, ProxmoxSharp owns discovery + post-create config + lifecycle.

- [x] Confirm the automated mode trigger (`mode=generated`) + full `var_*` surface (from `build.func`)
- [x] Define the shape → var mapping (added `spec.app`/`unprivileged`/`os`/`osVersion` to the schema)
- [x] Build the renderer — `Infrastructure/deploy/Deploy-Shape.ps1` (PowerShell; dry-run default, existence guard, `-Apply`)
- [x] Dry-run verified against `examples/servarr.lxc.yaml`
- [x] **Live smoke test passed** — CT 3099 created on hpe-01 over SSH, verified running via MCP, destroyed. Needed `TERM=xterm` in the remote env (community-scripts call `clear`, which fails over no-TTY SSH) — now baked into the renderer.
- [ ] Catalogue which community-scripts back our LXCs (map app → script)
- [ ] Post-create host-level mounts (NFS) — not covered by community-scripts; deferred to ProxmoxSharp lifecycle

---

### BL-014 — ProxmoxSharp CLI (dotnet global tool)
**Status:** Idea — captured, not started
**Priority:** Medium
**Tags:** `proxmoxsharp` `cli` `dotnet-tool` `dx`
**Relates to:** [BL-009](#bl-009--c-native-iac-discover-read-only-state-import)

Build a `dotnet tool`-installable CLI inside the ProxmoxSharp repo that wraps the
library — so the Proxmox read/discover surface is usable straight from the shell
(and from Claude) without writing a host program each time. Depends on the
ProxmoxSharp read path landing first (BL-009).

- [ ] CLI project in `vendor/ProxmoxSharp` packaged as a `dotnet tool` (`PackAsTool`)
- [ ] Commands wrapping the read path (e.g. `proxmoxsharp nodes`, `… discover`)
- [ ] Auth via env/API-token config; read-only first
- [ ] Publish so `dotnet tool install -g` works (shares packaging story with BL-009 M5)

---

## Done

- **BL-001** — Teleport deployed (LXC 9904); Keycloak removed. *(confirmed 2026-05-29)*
- **BL-003** — desktop-01 VMs identified (Plex-VM, gaming-vm-01/02). *(2026-05-29)*
- **BL-004** — jellyseerr / qbittorrent-clone / vpn-china all removed. *(2026-05-29)*
- **BL-005** — Keycloak won't-do; superseded by Teleport. *(2026-05-29)*
