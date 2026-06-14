# Gaming stack

GPU-passthrough VMs on `desktop-01` — for playing games (streamed to a MacBook)
and for testing **ErpForFactoryGames** against real games under Proton.

> Scaffolded **locally** for now (not yet a submodule). Promote to
> `Chrison-dev/Homelab.Stacks.Gaming` once the shape stabilises.
> Plan: [#115](https://github.com/Chrison-dev/Homelab/issues/115) ·
> [`docs/plans/115-gaming-vm-steamos.md`](../../docs/plans/115-gaming-vm-steamos.md).

## Members

VMID block **1000–1099** (declared in [`stack.yaml`](stack.yaml)).

| VMID | Member | Image | GPU | Status |
|------|--------|-------|-----|--------|
| 1002 | [windows](windows.vm.yaml) | Windows 11 | `AMD_Radeon_RX6600` mapping | **adopted** — the proven passthrough recipe; ErpForFactoryGames test bed |
| 1003 | [bazzite](bazzite.vm.yaml) | Bazzite-deck (SteamOS-style) | `AMD_Radeon_RX6600` mapping | **adopted** — passthrough to (re)apply |
| 1001 | Plex-VM | — | — | **unmanaged** (hand-built) |

**1002** and **1003** are IaC-managed (adopted). **1001** (Plex-VM) is intentionally
left untouched (same pattern as CT 2005 in the DevOps stack). The single Radeon RX
6600 is **shared** between 1002 and 1003 — only one runs at a time, both `onboot: false`.

## Hardware reality (desktop-01)

- Ryzen 5 3600 (6c/12t), **no integrated GPU**, **16 GB RAM**, PVE 9.2.2.
- **One** discrete GPU (`0000:09:00`) → only **one** gaming VM runs at a time, and
  the host is headless while it does. Host VFIO/IOMMU is already proven by VM 1002.
- 16 GB RAM is tight: a 12 GB VM + the running LXCs oversubscribes — right-size or
  run when LXCs are idle.

## Deploying

These are `kind: VM` shapes for the `homelab/v1` contract, provisioned by the
**ProxmoxSharp VM write path** (planned, #115) — *not* by the LXC
`Deploy-Shape.ps1` path. Until that lands, the shape documents desired state.

```
proxmoxsharp vm plan  ./stacks/Gaming/bazzite.vm.yaml   # dry-run diff (planned)
proxmoxsharp vm apply ./stacks/Gaming/bazzite.vm.yaml --confirm
```

Adopting 1003 should plan as **only** `+ hostpci0: 0000:09:00` — never a recreate.

## Streaming to the MacBook

- **Sunshine** (guest) + **Moonlight** (Mac) — low-latency primary path.
- **Steam Remote Play** — zero-config fallback.

## Re-deployability

The VM *shell* (config, disks, passthrough) is fully IaC. The **guest OS install +
Steam login is a one-time manual step**; capture a post-install snapshot as the
re-deploy baseline (or automate via Bazzite auto-install later — #115 Phase D).
