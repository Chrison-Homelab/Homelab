# ADR-0001 — IaC tooling & approach: build our own, in C#

- **Status:** Accepted
- **Date:** 2026-05-29
- **Deciders:** Chris

## Context

This repo is the GitOps hub for a **brownfield** homelab: 2–3 Proxmox nodes, 20+
LXCs, a Synology NAS, and a Unifi-managed network — almost all of it provisioned
**by hand** so far. We want to bring it under reproducible, Git-driven control.

Constraints and context that shape the decision:

- **Stacks are self-contained.** Each `Homelab.Stacks.*` submodule already deploys
  itself (Docker Compose + `bin/*.ps1` + Bitwarden + Cloudflare). So the open
  question is only about the **hub layer**: the nodes, the bare LXCs/VMs that
  *host* stacks, the NAS, and the network — not the stacks themselves.
- **This repo eats its own dogfood.** Chris owns a C# ecosystem that is built
  exactly for this: [Fallout](https://github.com/Fallout-build/Fallout) (a
  C#/.NET build system, NUKE successor), [ProxmoxSharp](https://github.com/Chrison-dev/ProxmoxSharp)
  and [SynoSharp](https://github.com/Chrison-dev/SynoSharp) (API clients).
- **The building blocks are early.** As of this ADR, ProxmoxSharp and SynoSharp
  are empty stubs, and Fallout's plugin SDK (`Fallout.Plugin.Sdk`) is not shipped
  — it lands in Fallout v12 (5 RFCs open). Chris owns that roadmap.

Conventional options considered: **OpenTofu (provision) + Ansible (config)**, or
**Ansible-first**. Both are mature and would work, but both sit *next to* this
ecosystem rather than *in* it, in two foreign languages (HCL/YAML).

## Decision

**Build our own C#-native IaC, run from this hub.**

1. **All provisioning runs from the hub**, under a new top-level **`/Infrastructure`**
   directory (capital I — the new world). Submodules **declare the *shape*** of
   what they need provisioned; they never run provisioning themselves.
2. **Shape = declarative YAML** validated against a schema in `/Infrastructure`.
   It's intentionally just data — a shape can be swapped for another format later
   without touching the engine.
3. **The engine is C#**, built on Chris's own clients (ProxmoxSharp, SynoSharp,
   later a Unifi client). It runs as a **Fallout build target / console app today**,
   and is **repackaged as a Fallout plugin** (`Fallout.Plugin.Proxmox`, …) once
   Fallout v12 ships the plugin SDK. The long-term vision: one tool suite that
   provisions nodes, spins up LXCs/VMs, and deploys into them.
4. **`/infra` is the old world, frozen.** The existing `infra/` (Ansible,
   OpenTofu, docker monitoring, proxmox community-scripts) stays as legacy and is
   **not extended**. New work happens in `/Infrastructure`. Migrate opportunistically.

### Roadmap: Define → Discover → Converge (brownfield order)

1. **Define** — establish the shape schema and prove it on one stack + the bare
   hosts. (This ADR + the initial `/Infrastructure` scaffold.)
2. **Discover (read-only)** — ProxmoxSharp reads the *live* cluster and grabs
   current state. This is the first real use of the client (read-only = low risk)
   and the "state import" step. It also auto-documents reality (`Devices.md`,
   `Services.md`). Discovery **informs** the shapes — we don't author desired
   state blind against 20+ hand-built LXCs.
3. **Converge (reproducible)** — only once shapes ⇄ reality reconcile, enable
   create/update/destroy, always plan/dry-run before apply.

See [`docs/plans/iac-csharp-native.md`](../plans/iac-csharp-native.md) for the
detailed plan and component breakdown.

## Consequences

**Positive**
- One language end-to-end (build + provision + deploy glue).
- ProxmoxSharp/SynoSharp/Unifi-client get battle-tested by real use — the bugs
  nothing else would find.
- Fully owned, fully debuggable, IDE-native. Maximal dogfooding.
- A reusable Fallout plugin falls out of it for the wider community.

**Negative / risks**
- **We are the IaC engine.** No free `plan`/state/drift — we design those.
  Discovery-first ordering and mandatory dry-run mitigate the blast radius.
- More code to own; slower to first value than `tofu apply`.
- Blocked-feeling until ProxmoxSharp is real — mitigated by starting with
  read-only discovery, which needs only a thin slice of the client.
- Plugin SDK is unshipped — mitigated by running as a Fallout target until v12.

## Notes

- Network (Unifi) gets the same treatment: explore via an MCP, then build a small
  C# client so network config is deployable too.
- A homelab dashboard (likely GitHub Pages on an owned domain) is a desirable
  downstream artifact, fed by discovery output.
