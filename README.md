# Homelab

> The single source of truth for my homelab. Infrastructure, configuration, and
> documentation as code — provisioned via GitOps, deployed by my own CI/CD.

This is an **eat-your-own-dogfood** repository. Everything that runs in the lab
is described here (or in a linked submodule), version-controlled, and reconciled
from Git. If it isn't in the repo, it doesn't exist.

---

## 🎯 Mission

1. **Infrastructure as Code** — provision and configure the homelab (Proxmox
   nodes, LXCs/VMs, Synology NAS, networking) declaratively. No manual clicking
   in web UIs where it can be avoided.
2. **GitOps-driven** — Git is the source of truth. Changes land via commits and
   are rolled out by **[Fallout](#-cicd-fallout)**, my own CI/CD system. Pushing
   to the repo is how the lab changes.
3. **Documented** — the network, devices, and architecture are described here so
   the lab is understandable and rebuildable by future-me (and anyone else).

## 🧱 Repository model

This is the **main repository** — the hub. Self-contained, reusable stacks live
in their **own `Homelab.Stacks.*` repositories and are linked here as Git
submodules**. The hub owns shared infrastructure, conventions, networking, docs,
and orchestration; each submodule owns one stack end to end.

```
Homelab (this repo)            ← hub: shared infra, networking, docs, CI/CD glue
├── stacks/
│   ├── Infrastructure         ← Homelab.Stacks.Infrastructure   (container hosts)
│   ├── Komodo                 ← Homelab.Stacks.Komodo           (Docker host mgmt)
│   ├── DevOps                 ← Homelab.Stacks.DevOps
│   └── ErpForFactoryGames     ← Homelab.Stacks.ErpForFactoryGames
└── Infrastructure, infra, docs, src, …   ← shared, cross-cutting concerns
```

> The `*arr` media stack is **not** a submodule: it runs as individual LXCs and
> will be recreated from discovered state (Define → Discover → Converge), not
> from a hand-authored compose stack.

**Rule of thumb:** a thing becomes its own `Homelab.Stacks.*` submodule when it
is a self-contained service/stack with its own lifecycle. Cross-cutting concerns
(networking, monitoring, base provisioning, conventions) stay in the hub.

### Stack conventions

Every `Homelab.Stacks.*` repo follows the same shape (see
[`ErpForFactoryGames`](https://github.com/ChrisonSimtian/Homelab.Stacks.ErpForFactoryGames)
as the reference):

- **`compose.yml`** + per-service `*.env` / `stack.env` — the stack itself.
- **`ingress.json`** — Cloudflare Tunnel ingress rules (TLS terminates at the
  edge; plain HTTP inside the Docker network).
- **`bin/*.ps1`** — deploy lifecycle (`provision.ps1`, `deploy.ps1`,
  `load-secrets.ps1`). Runs on a Proxmox LXC, SSH from the dev box.
- **Secrets from Bitwarden** — fetched at deploy time, never written to disk
  (file fallback only until BW is fully adopted).
- **`README.md`** with an architecture diagram + first-time setup, **`LICENSE`**.

## 🧭 Principles

Inherited from the [Overseer agent](.github/agents/global-agent.md) and applied
to everything in this repo:

- **IaC-first** — declarative over imperative; UIs are a last resort.
- **Idempotent** — running the same thing twice changes nothing the second time.
- **Reversible** — changes can be rolled back; destructive steps are explicit.
- **Modular** — small, composable pieces; link and reuse instead of duplicating.
- **Explicit over implicit** — no hidden behavior.
- **Multi-everything** — assume multiple nodes, multiple NAS, multiple services.

## 🗂️ What lives where

| Path | Purpose |
| --- | --- |
| `Infrastructure/` | **New world** — C#-native, reproducible provisioning. Shapes + (soon) engine. |
| `docs/` | [Network](docs/Network.md), [devices](docs/Devices.md), [services](docs/Services.md), [backlog](docs/Backlog.md), [ADRs](docs/adr), and plans. |
| `infra/` | **Legacy world** — Ansible / OpenTofu / docker monitoring / proxmox scripts. Frozen, not extended. |
| `src/Proxmox/` | Bash + PowerShell scripts for node bootstrap & inventory. |
| `containers/` | Local dev/test environments (Proxmox, DSM, Debian). |
| `.github/agents/` | AI agent personas that enforce repo conventions. |
| `stacks/` | Submodules — one self-contained stack per repo. Each declares its `shape`. |

> **Tooling decision (locked in):** we build our **own C#-native IaC**, run from
> `/Infrastructure`, dogfooding ProxmoxSharp / SynoSharp / Fallout. Submodules
> declare *shape* (YAML); the hub provisions. The old `/infra` (OpenTofu/Ansible)
> is legacy and frozen. See [ADR-0001](docs/adr/ADR-0001-iac-tooling.md).

## ⚙️ CI/CD: Fallout

GitOps is driven by **[Fallout](https://github.com/ChrisonSimtian/Fallout)** — my
own C#/.NET build system (a successor to NUKE). Instead of hand-written YAML, the
build/deploy logic is a **C# console app**, and Fallout *generates* the
`.github/workflows/*.yml` that GitHub Actions executes. Build steps live in code,
with full IDE support, type-safety, and debugging.

The intent: commits to this repo (and its submodules) drive deployment to the lab
via GitHub Actions — no out-of-band manual deploys.

```bash
dotnet tool install -g Fallout.Cli   # or pin per-repo via .config/dotnet-tools.json
fallout                              # run the build locally
```

Wiring the Fallout build project into this repo is in-progress (see
[Roadmap](#-roadmap)).

## 🌐 Network & devices

See [`docs/Network.md`](docs/Network.md) for the VLAN-segmented architecture
(Unifi Cloud Gateway) and [`docs/Devices.md`](docs/Devices.md) for the hardware
inventory.

## 🛠️ Development & testing

Local dev environments, tool installation, and how to test scripts before they
touch real nodes are documented in [`docs/Development.md`](docs/Development.md).

## 🗺️ Roadmap

- [x] Wire the 5 existing `Homelab.Stacks.*` repos in as submodules under `stacks/`.
- [x] Lock in the IaC approach — C#-native, run from `/Infrastructure` ([ADR-0001](docs/adr/ADR-0001-iac-tooling.md)).
- [ ] **Define** the shape contract ([BL-008](docs/Backlog.md)) — in progress.
- [ ] **Discover** live Proxmox state read-only via ProxmoxSharp ([BL-009](docs/Backlog.md)).
- [ ] **Converge** — reproducible provisioning, plan/apply ([BL-010](docs/Backlog.md)).
- [ ] Unifi discovery + C# client ([BL-011](docs/Backlog.md)); homelab dashboard ([BL-012](docs/Backlog.md)).
- [ ] Add a Fallout build project + private-submodule CI auth ([BL-007](docs/Backlog.md)).

## 🔗 Useful links

- [Synology DSM File Station API](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Package/FileStation/All/enu/Synology_File_Station_API_Guide.pdf)
- [Proxmox VE API](https://pve.proxmox.com/wiki/Proxmox_VE_API)
