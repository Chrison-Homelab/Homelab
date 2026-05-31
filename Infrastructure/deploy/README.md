# Infrastructure/deploy — community-scripts over SSH (BL-013)

The **create** mechanism for the C#-native IaC roadmap: turn a declarative
`shape.yaml` into a [community-scripts.org](https://community-scripts.org/)
automated-mode invocation and run it over SSH on the target Proxmox node.

This is the unblocked stopgap that gets reproducible LXC bring-up working *today*,
without the C#/Fallout engine (currently blocked on the Fallout NuGet channel).
The **shape contract is the stable interface** — ProxmoxSharp will later own
config + lifecycle (update/destroy) and can replace this renderer without
changing shapes. See [ADR-0001](../../docs/adr/ADR-0001-iac-tooling.md) and the
[BL-013 plan](../../docs/plans/BL-013-community-scripts-deploy.md).

## How it works

`Deploy-Shape.ps1` reads a shape, maps it to the community-scripts `var_*`
surface, and emits:

```bash
mode=generated var_ctid=… var_hostname=… … bash -c "$(curl -fsSL …/ct/<app>.sh)"
```

`mode=generated` is the key: it bypasses the interactive whiptail menu
(`build.func`: `CHOICE="${mode:-${1:-}}"`), so the script runs cleanly over SSH
with no TTY, consuming the exported `var_*` directly.

## Usage

```powershell
# Dry-run (default) — prints the exact command, mutates nothing:
./Deploy-Shape.ps1 -ShapePath ../examples/servarr.lxc.yaml

# Deploy for real — checks the CTID is free, then runs over SSH:
./Deploy-Shape.ps1 -ShapePath ./mything.lxc.yaml -Apply
```

Requires `pwsh` + the `powershell-yaml` module
(`Install-Module powershell-yaml -Scope CurrentUser`).

### SSH access

The renderer SSHes to **`root@<spec.node>`** — i.e. by node *name* (`hpe-01`).
Your SSH client must resolve that name and authenticate with a key. The simplest
setup is an alias in `~/.ssh/config`:

```
Host hpe-01
    HostName 192.168.179.3   # legacy mgmt IP — see docs/Devices.md (migration: BL-002)
    User root
```

Override the target ad hoc with `-Node <host-or-ip>`.

## Stack inheritance

If a shape sets `metadata.stack`, the renderer loads the sibling `stack.yaml`
(`kind: Stack`) in the same folder and merges its `spec.defaults` *underneath*
the member (member wins; nested objects like `network`/`source` merge key-by-key).
So a member only declares what differs:

```
Infrastructure/devops/
  stack.yaml              # ctidRange 3000-3999 + defaults (node, os, network, …)
  forgejo.lxc.yaml        # just: app, ctid 3000, cores/memory/disk
  forgejo-runner.lxc.yaml # + source.channel: dev   (overrides stack default)
```

The stack's `ctidRange` is also a **guard rail**: an explicit member `ctid`
outside `[start,end]` is rejected.

## Script source (channel) — stable vs dev

`spec.source` chooses which community-scripts repo/ref the `ct/<app>.sh` comes from:

| `spec.source`            | resolves to                                        |
|--------------------------|----------------------------------------------------|
| *(omitted)* / `channel: stable` | `community-scripts/ProxmoxVE@main` (stable) |
| `channel: dev`           | `community-scripts/ProxmoxVED@main` (in-development, e.g. `forgejo-runner`) |
| `repo: owner/name`       | explicit override of the repo                      |
| `ref: <sha\|tag\|branch>` | pin a ref (recommended for `dev` — those scripts move) |

`-BaseUrl <url>` still hard-overrides everything.

## Shape → var mapping

| shape field            | var                     |
|------------------------|-------------------------|
| `spec.app`             | selects `ct/<app>.sh`   |
| `spec.source`          | the `curl` base URL (channel/repo/ref) |
| `metadata.name`        | `var_hostname`          |
| `spec.ctid`            | `var_ctid` (explicit int required; `auto` rejected on this path) |
| `spec.cores`           | `var_cpu`               |
| `spec.memory`          | `var_ram`               |
| `spec.disk`            | `var_disk`              |
| `spec.unprivileged`    | `var_unprivileged`      |
| `spec.os`              | `var_os`                |
| `spec.osVersion`       | `var_version`           |
| `spec.storage`         | `var_container_storage` |
| `spec.templateStorage` | `var_template_storage`  |
| `spec.network.bridge`  | `var_brg`               |
| `spec.network.vlan`    | `var_vlan`              |
| `spec.network.ipv4`    | `var_net`               |
| `spec.network.gateway` | `var_gateway`           |
| `spec.network.ipv6`    | `var_ipv6_method`       |
| `spec.network.mtu`     | `var_mtu`               |
| `spec.nameserver`      | `var_ns`                |
| `spec.searchdomain`    | `var_searchdomain`      |
| `spec.features.nesting`| `var_nesting`           |
| `spec.features.fuse`   | `var_fuse`              |
| `metadata.tags` + `spec.tags` | `var_tags` (`;`-joined, deduped) |

The schema (`../schema/shape.schema.json`) models the **full** Proxmox LXC
surface (swap, cpulimit, startup, devices, multiple `networks`, `lxcRaw`, …);
fields without a `var_*` above are **converge-only** (ProxmoxSharp / BL-010) and
are simply not emitted by this create path.

## Guardrails & limits

- **Dry-run by default.** `-Apply` is required to mutate. Mirrors the engine's
  eventual `plan`/`apply` split.
- **Explicit CTID only.** `ctid: auto` is rejected here — auto-allocation from a
  stack range is an engine (ProxmoxSharp / BL-010) concern, kept deliberate.
- **Create-only + existence guard.** community-scripts create is **not
  idempotent**; before `-Apply` the renderer queries cluster resources and
  refuses if the CTID already exists. Update/destroy = ProxmoxSharp (BL-010).
- **Mounts are not handled.** A shape's NFS/host `mounts` are warned about and
  skipped — configure them as a post-create, host-level step (per the
  NFS-at-host convention in `CLAUDE.md`). Tracked for the lifecycle work.
- **LXC only.** `kind: VM` / `NASShare` are out of scope here.
