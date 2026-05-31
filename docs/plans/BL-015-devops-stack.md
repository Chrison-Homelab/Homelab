# Plan: BL-015 — DevOps stack (Forgejo, runners, Woodpecker)

**Issue:** [#51](https://github.com/Chrison-dev/Homelab/issues/51) (Project #7 "Homelab Backlog") ·
**Relates to:** [BL-013 community-scripts deploy](BL-013-community-scripts-deploy.md),
[BL-009/010 C#-native IaC](iac-csharp-native.md), [ADR-0001](../adr/ADR-0001-iac-tooling.md)
**Status:** Planned — 2026-05-31. Schema + shapes + renderer wiring landed and
dry-run-verified; deployment (`-Apply`) is the next, opt-in step.

## Goal

Self-host the dev/CI toolchain on the Proxmox cluster, defined as IaC (shapes +
the BL-013 community-scripts renderer) rather than hand-built containers:

- **Forgejo** — self-hosted Git forge.
- **Forgejo Runner** — CI/CD executor for Forgejo Actions.
- **GitHub Runner** — a self-hosted runner offered to GitHub projects (new,
  independent of the existing CT 2005).
- **Woodpecker CI** — second CI engine to evaluate (parked, see below).

## Confirmed by research (2026-05-31)

- **Community-script availability** (GitHub API on `ct/`):
  - `forgejo.sh` → **`community-scripts/ProxmoxVE`** (stable). Defaults 2 CPU / 2048 MB / 10 GB / Debian 13 / unprivileged.
  - `github-runner.sh` → **`community-scripts/ProxmoxVE`** (stable). Defaults 2 / 2048 / 8 / Debian 13.
  - `forgejo-runner.sh` → **`community-scripts/ProxmoxVED`** (the *development* repo, trailing **D**). Real and runnable; the `community-scripts.org/scripts/forgejo-runner` page is marked "In Development". Defaults 2 / 2048 / 8 / Debian 13.
  - **woodpecker** → **no script** in either `ProxmoxVE` or `ProxmoxVED`, no open PR. → parked.
- **Cluster** (Proxmox MCP, read-only): `hpe-01`, `nuc-01`, `desktop-01` online; next free VMID 2009; `desktop-01` is the largest node (12 cores, big local-lvm) → CI host.
- **Existing github-runner CT 2005** (hpe-01, tags `ci;community-script`) is running. Per decision it is **left untouched and unmanaged by IaC**; the new runner is independent.
- **VLAN reality**: new community-script CTs (e.g. traefik 2007) get a single tagged interface `net0=name=vlan1010,bridge=vmbr0,tag=1010,ip=dhcp`. VLAN tagging on the interface *is* done; the `vlan-1010` string in Proxmox tags is UI ceremony.

## Decisions (with Chris, 2026-05-31)

1. **Provisioning** — pure community-scripts for all (no custom installs). Forgejo-runner via the **dev** channel.
2. **CT 2005** — leave untouched; roll out a **new, independent** github-runner.
3. **Woodpecker forge backend** — Forgejo vs GitHub: decide later (currently moot — parked).
4. **CTID ranges per stack** — DevOps stack owns **3xxx** (`3000–3999`). `9xxx` is reserved for short-lived test CTs.
5. **CTID allocation** — **explicit by default**; omission is an error (no silent allocation). `ctid: auto` is an explicit, in-file opt-in for the rare case (engine-only; the community-scripts path rejects it).
6. **Shape schema** — make it **feature-complete** for Proxmox (every relevant LXC option settable, most optional).

## Deliverables — landed

### 1. Schema — `Infrastructure/schema/shape.schema.json`
- New `kind: Stack` — owns `spec.ctidRange {start,end}` + `spec.defaults` (an LXC-shaped block members inherit).
- New `spec.source {channel: stable|dev, repo, ref}` for LXC — selects the community-scripts repo (`stable`→ProxmoxVE, `dev`→ProxmoxVED) + ref pin.
- `spec.ctid` now `oneOf [integer, "auto"]` and **required** for LXC (omission → schema error).
- **Feature-complete LXC surface**: `arch, cpulimit, cpuunits, swap, protection, onboot, startup, features{nesting,keyctl,fuse,mknod,mount,forceRwSys}, network` (single, community-scripts path) + `networks[]` (multi-NIC, converge), `nameserver, searchdomain, mounts[]` (full mp opts), `devices[]`, `timezone, console, pool, hookscript, rootfsOptions, lxcRaw[]` escape hatch, `sshAuthorizedKey`, `tags`.
- Per-kind strictness via `if/then` (LXC + Stack strict with `additionalProperties:false`; Node/VM/NASShare stay permissive). Validated meta + against all shapes + negative cases (`jsonschema`).

### 2. Stack + member shapes — `stacks/DevOps/` (submodule `Homelab.Stacks.DevOps`)
- `stack.yaml` — `ctidRange 3000–3999`; defaults: `node desktop-01`, `channel stable`, Debian 13, unprivileged, `local-lvm`, `network vlan 1010 / dhcp`, `Pacific/Auckland`, tags `[devops, community-script]`.
- `forgejo.lxc.yaml` — ctid **3000**, 2/2048/**20** GB (bumped for repo growth), stable.
- `forgejo-runner.lxc.yaml` — ctid **3001**, 4/4096/16, **`source.channel: dev`**, `features.nesting`.
- `github-runner.lxc.yaml` — ctid **3002**, 4/4096/16, stable, `features.nesting`.
- `README.md` + CTID map. **Woodpecker reserved at 3003** (shape deferred until a script exists).

### 3. Renderer — `Infrastructure/deploy/Deploy-Shape.ps1`
- Resolves `spec.source` → `raw.githubusercontent.com/<repo>/<ref>/ct/<app>.sh` (channel map + `repo`/`ref` overrides; `-BaseUrl` still hard-overrides).
- Merges the owning `stack.yaml` `spec.defaults` under the member (recursive; member wins).
- Rejects `ctid: auto`; validates explicit ctid is inside the stack range.
- New var mappings: `var_brg, var_mtu, var_ns, var_searchdomain, var_nesting, var_fuse`; tags = stack defaults + member, deduped.
- Dry-run verified for all three members (correct repos: forgejo/github-runner→ProxmoxVE, forgejo-runner→ProxmoxVED).

## Deploy sequence (opt-in — `-Apply`, not yet run)

1. **Forgejo (3000)** first — runners/Woodpecker depend on a forge.
   `./Infrastructure/deploy/Deploy-Shape.ps1 -ShapePath ./stacks/DevOps/forgejo.lxc.yaml -Apply`
   Post-create (manual): admin user, base URL, TLS via Traefik (2007)/cloudflared (2001).
2. **github-runner (3002)** — independent; needs a GitHub repo/org + registration token (manual).
3. **forgejo-runner (3001)** — after Forgejo is up; needs the Forgejo URL + runner registration token (manual).
4. Verify each via Proxmox MCP / `proxmoxsharp discover`; record in `docs/Services.md`.

## Parked / deferred

- **Woodpecker CI** — no community script in either repo yet. Reserved **CTID 3003**; revisit when upstream ships one (then decide Forgejo vs GitHub backend). Not deploying a custom install under the "community-scripts only" decision.
- **Post-create config, dependencies & secrets** — shapes are provision-only today; registration tokens / admin setup / TLS are manual. Tracked as its own task (→ folds into BL-010 converge): how to model `dependsOn`, app config, and secret references.
- **NFS placement** — separate exploration (host-level mount + bind vs in-container NFS; performance + mount-race robustness). Forgejo repo data is on `local-lvm` for now.

## Guardrails

- **Plan before apply** — renderer dry-run by default; no cluster mutation in this plan.
- **Existence guard** — create is not idempotent; renderer refuses an existing CTID.
- **2005 untouched** — the new runner is a separate CT; note in `docs/Services.md` that 2005 is intentionally unmanaged so discovery doesn't flag it as drift.
- **Dev-channel caution** — `forgejo-runner` is an in-development script; pin `source.ref` to a SHA once it stabilises.
