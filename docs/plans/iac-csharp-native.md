# Plan: C#-native IaC for the Homelab hub

Implements [ADR-0001](../adr/ADR-0001-iac-tooling.md). Build our own provisioning
engine in C#, run from `/Infrastructure`, dogfooding ProxmoxSharp / SynoSharp /
Fallout. Submodules declare *shape*; the hub provisions.

## Mental model

```
   Homelab.Stacks.* (submodules)            Homelab hub  (/Infrastructure)
   ─────────────────────────────            ──────────────────────────────
   shape.yaml  ── "I need an LXC:    ──▶     schema/         validate shapes
                   2c/2G, VLAN 1010,         nodes/          hub-owned host shapes
                   NFS mount, CTID …"        engine (C#)     read shapes →
                                                             ProxmoxSharp/SynoSharp
                                                             → plan → apply
                                              ▲
                                              │ runs as a Fallout target today,
                                              │ a Fallout plugin once v12 SDK ships
```

- **Shape** = declarative YAML, validated against `Infrastructure/schema/`. Just
  data; replaceable.
- **Submodules** own the shape of *their* infra. The **hub** owns the shape of
  non-stack infra (the bare nodes / NAS) under `Infrastructure/nodes/`.
- **Engine** = C#, on Chris's own API clients. Fallout target now → plugin later.

## Phase 1 — Define  (this phase)

- [x] ADR-0001 — decision recorded.
- [x] `/Infrastructure` scaffold + shape JSON Schema.
- [x] One reference shape (`examples/servarr.lxc.yaml`) grounded in the real
      community-scripts vars (CTIDs 3001–3003, VLAN 1010, hpe-01, NFS).
- [x] Host shape stubs for the three nodes (specs marked TODO — confirmed in
      Phase 2 by discovery, not guessed).
- [ ] Iterate the schema as we learn what fields provisioning actually needs.

## Phase 2 — Discover (read-only) — `BL-009`

Requires: a Proxmox MCP (to explore interactively) + a thin ProxmoxSharp read path.

- [ ] Stand up a Proxmox MCP; explore the cluster interactively.
- [ ] Give ProxmoxSharp a real skeleton: auth + read endpoints (nodes, LXCs, VMs,
      storage, network) — **read-only only**.
- [ ] `discover` command: dump live cluster state to structured output.
- [ ] Auto-generate / reconcile `docs/Devices.md` + `docs/Services.md` from it.
- [ ] Reconcile hand-written shapes against discovered reality; fix the shapes.

## Phase 3 — Converge (reproducible) — `BL-010`

> **Create mechanism (BL-013):** Chris already provisions with
> [community-scripts.org](https://community-scripts.org/), which run over SSH and
> now support a predefined-parameter automated mode. Likely split: render a shape
> → community-script invocation for **create**, ProxmoxSharp for **config +
> lifecycle** (start/stop/destroy). Decided during BL-010/BL-013.

- [ ] ProxmoxSharp write path (create/update/destroy LXC + VM, config).
- [ ] `plan` — diff desired shapes vs. discovered state, **no mutation**.
- [ ] `apply` — converge, gated behind an explicit confirm; dry-run by default.
- [ ] Idempotency + reversibility guarantees (re-run = no-op; destroy is explicit).

## Parallel tracks

- **SynoSharp + NAS shapes** — same Define→Discover→Converge for the Synology NAS.
- **Unifi** — `BL-011`: explore via MCP, build a small C# Unifi client, bring
  network config under the same model.
- **Fallout plugin** — once v12's `Fallout.Plugin.Sdk` ships, repackage the engine
  as `Fallout.Plugin.Proxmox` (tracked with the Fallout roadmap, not here).
- **Dashboard** — `BL-012`: a homelab dashboard (GitHub Pages on an owned domain)
  fed by discovery output.

## Guardrails (from the Overseer conventions)

- **Read before write.** Discovery is read-only and lands first.
- **Plan before apply.** Never mutate without a reviewed diff.
- **Idempotent + reversible.** Re-running converges to the same state; destructive
  steps are explicit and opt-in.
