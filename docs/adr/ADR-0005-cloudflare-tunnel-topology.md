# ADR-0005 — Cloudflare tunnel topology: per-stack tunnels, HA via replicas, Teleport as admin front door

- **Status:** Accepted
- **Date:** 2026-06-13
- **Deciders:** Chris
- **Relates to:** [ADR-0001](ADR-0001-iac-tooling.md),
  [BL-001 Teleport](../plans/BL-001-teleport.md),
  Teleport exposure [#117](https://github.com/Chrison-Homelab/Homelab/issues/117),
  cloudflared redeploy [#118](https://github.com/Chrison-Homelab/Homelab/issues/118)

## Context

Remote access into the homelab (no VPN from work) rides on Cloudflare Tunnel:
outbound-only connectors, no inbound ports. The current setup is ad-hoc and flaky:

- **Monolithic, hand-managed** `cloudflared-01` (CT 2001, hpe-01) fronts everything
  (`proxmox.chrison.dev`, `forgejo.chrison.dev`, …) as a single tunnel.
- A second tunnel `Homelab.Stacks.DevOps` (CT 3004) and the ERP app's own embedded
  tunnel (CT 2008) exist alongside it — mixed ownership, no consistent model.
- **nuc-01 has no connector**, so services there (Teleport 9904, Traefik 2007) egress
  *cross-node* through hpe-01's connector → nuc-01's reachability is coupled to hpe-01.

The first instinct (logged in #118) was **one connector per node**. On reflection that
optimises the wrong axis. Cloudflare's own model separates two concerns we had conflated:

- A **tunnel** is a logical ingress unit (own credentials + ingress ruleset + DNS). This
  is the **isolation / ownership / blast-radius** axis.
- A **connector** (`cloudflared` running with a tunnel's token) is the runtime. **Multiple
  connectors can serve one tunnel** — Cloudflare load-balances and fails over between them.
  This is the **availability (HA)** axis.

So the flakiness is an *availability* problem (solved by replicas), not a reason to mint a
tunnel per node. And **node is the wrong boundary** for tunnels because:

1. **Services move between nodes** — live migration, the GPU-pinned Gaming VM (desktop-01),
   the planned mergerfs gateway VM (ADR-0004), rebalancing. Per-node tunnels churn ingress
   rules every relocation.
2. **A node hosts services from multiple VLANs** (Homelab 1010, Consumer 1020, IoT 1040, a
   future per-stack/DMZ zone). A per-node connector must reach *across* VLANs → punch
   inter-VLAN firewall holes from every node into every zone → **defeats the VLAN isolation**.

Most services sit on Homelab (1010) today; per-stack VLANs are partly aspirational, so the
topology must accommodate a stack *gaining* its own VLAN without re-architecting.

## Decision

**Slice tunnels by stack / security-zone, not by node. Get HA from connector replicas.
Use Teleport as the authenticated front door for admin UIs; expose machine/public
endpoints directly.**

1. **One tunnel per stack.** Tunnel lifecycle = stack lifecycle (stacks are the IaC unit /
   submodules). Generalises the existing `Homelab.Stacks.DevOps` pattern; matches CLAUDE.md
   "self-contained, removable additions." Deploy/destroy a stack and its tunnel comes/goes
   with it. A leaked tunnel token exposes only that stack.

2. **Connector lives inside the stack's VLAN/zone.** It only egresses services in that zone
   → **zero cross-VLAN holes**. When a stack later gets its own VLAN, the connector follows
   it in; no topology change.

3. **HA = replicas, not more tunnels.** Run 2+ connectors (different nodes) for tunnels that
   must survive a single node loss; Cloudflare fails over automatically. Single-node stacks
   (Gaming pinned to desktop-01, DevOps CI muscle on desktop-01) get one connector — if that
   node is down the stack is down anyway, so HA buys nothing.

4. **Front-door rule — *human admin UI → behind Teleport App Access; machine/public
   endpoint → direct hostname* (hybrid).** cloudflared is dumb transport; Teleport is the
   authenticated, audited door for admin UIs (Proxmox, Grafana, *arr, Home Assistant, PDM).
   Things that *can't* do interactive SSO (git/webhook clients, Plex client apps) or are
   public (the ERP app) get their own direct hostname, gated by Cloudflare Access where the
   audience is human.

5. **Break-glass exception.** Routing *everything* through Teleport is a bootstrap trap — a
   wedged Teleport CT would lock you out of the Proxmox UI you need to fix it. So the
   **Proxmox node UIs + PDM stay reachable on the HA `core` tunnel behind CF Access** as the
   escape hatch; all other admin UIs go via Teleport.

### Target tunnels

| Tunnel | Connector(s) | Fronts | Auth |
|---|---|---|---|
| **`core`** | 1010, **2 replicas** (hpe-01 + nuc-01) | `teleport.chrison.dev`; Proxmox node UIs + PDM | Teleport is the door; Proxmox/PDM behind CF Access (break-glass) |
| **`devops`** (exists, CT 3004) | desktop-01 / 1010 | `forgejo.chrison.dev` + webhooks | Direct — git/webhook clients can't SSO |
| **`media`** | media zone | `plex.chrison.dev` (direct); *arr UIs | Plex direct (clients); arr UIs via Teleport |
| **`monitoring`** | its zone | Grafana | via Teleport |
| **`household`** | its zone | Home Assistant | via Teleport |
| **`gaming`** | desktop-01 / Gaming zone | — | usually no public ingress |
| **`erp`** (exists, CT 2008) | public/DMZ zone | `erp.chrison.dev` | Public, no gate — end users |

## Alternatives considered

- **One connector per node** (the original #118 sketch) — rejected: optimises availability
  by the wrong means (node ≠ HA unit), churns ingress as services migrate, and forces
  cross-VLAN reachability from every node, defeating VLAN isolation.
- **Single monolithic tunnel + many replicas** — simplest ingress, good HA, but one ruleset
  for everything: maximal blast radius on token leak, no stack-level removability, and the
  connector still straddles all VLANs. Keeps the current mess, just more available.
- **One tunnel per VLAN/zone** (not per stack) — closer than per-node, but stacks don't map
  1:1 to VLANs today and the IaC unit is the *stack*; per-stack subsumes this (a stack with
  its own VLAN just lands its connector there).
- **Everything behind Teleport (no direct hostnames)** — smallest public surface, but the
  bootstrap trap (no break-glass) and can't serve machine/public clients (webhooks, Plex,
  ERP). Rejected in favour of the hybrid + break-glass.

## Consequences

- **+** Tunnel boundary tracks the IaC unit; deploy/destroy a stack and its ingress goes
  with it. Blast radius per token = one stack. VLAN isolation preserved (connector in-zone).
  HA where it matters (`core`) without per-node sprawl. Minimal public surface — most admin
  UIs sit behind one authenticated Teleport door.
- **−** More tunnels/connectors to provision than a single monolith (mitigated: each is a
  reusable IaC shape mirroring `cloudflared.lxc.yaml`).
- **−** The hybrid means two access patterns to remember (Teleport door vs direct hostname);
  documented per service in `Services.md`.
- **−** `core` HA needs a DHCP reservation (or static) per connector so the tunnel target is
  stable across reboots (Teleport CT 9904 is on DHCP today — see #117).
- **−** Admin access leans on Teleport uptime; mitigated by the Proxmox/PDM break-glass on
  the HA `core` tunnel.

## Migration (sketch — full plan in #118)

**Add-only on Cloudflare** (CLAUDE.md): the `chrison.dev` zone is hand-managed; never modify
existing tunnels/records. Stand up each new per-stack tunnel *alongside* the monolith → wire
its connector inside the stack's zone as an IaC shape → cut hostnames over **one at a time**,
verifying each → **retire the monolithic CT 2001 ingress last**, only once every hostname has
moved. Reversible at every step; Chris drives the Cloudflare dashboard side, shapes prepped
in-repo.

## Out of scope

- Finishing Teleport exposure + SSH-agent rollout (#117 / BL-001 Phases 2–3).
- Per-stack VLAN segmentation itself (network design; this ADR only assumes the tunnel model
  *accommodates* it).
- Choosing CF Access identity providers / policies (operational).
