# BuildLab stack

Windows dev/build VMs on `desktop-01` — for testing the **Fallout .NET build
framework** across multiple Visual Studio MSBuild toolchains (2019 / 2022 / 2026,
IDE + Build Tools).

> Scaffolded **locally** for now (not yet a submodule). Promote to
> `Chrison-dev/Homelab.Stacks.BuildLab` once the shape stabilises.
> Plan: [`docs/plans/buildlab-windows-vm.md`](../../docs/plans/buildlab-windows-vm.md).

## Members

VMID block **1100–1199** (declared in [`stack.yaml`](stack.yaml); members inherit its `defaults`).

| VMID | Member | Image | Status |
|------|--------|-------|--------|
| 1100 | [buildvm](buildvm.vm.yaml) | Windows 11 + VS 2019/2022/2026 (IDE + Build Tools) | **greenfield** — created fresh via the unattended pipeline |

`buildvm` is **greenfield** (the engine creates the VM + its volumes from scratch),
unlike the Gaming stack's *adopted* VMs. No GPU passthrough — it is a headless build
box and keeps the emulated display so the Proxmox console + RDP work.

## Hardware reality (desktop-01)

- Ryzen 5 3600 (6c/12t), **16 GB RAM**, the strongest CPU in the cluster and the
  node that already runs the proven Win11 recipe (gaming VM 1002).
- 16 GB is tight: the VM runs **on-demand at 12 GB** (`onboot: false`) so it borrows
  RAM only while testing. The gaming VMs are also on-demand → effectively mutually
  exclusive on RAM with this box (accepted). Raise the 12 GB once desktop-01 sheds LXCs.
- Disk is **150 GB thin** on `local-lvm` (Win11 + 6 lean .NET-build VS installs +
  .NET SDKs + build artifacts). Far smaller than a C++/full-IDE box because the
  workloads are minimal MSBuild/.NET. Confirm the thin pool has room before applying.

## Deploying

`kind: VM` shapes for the `homelab/v1` contract, provisioned by the **ProxmoxSharp
VM write path** via the `homelab-infra` engine — *not* the LXC `Deploy-Shape.ps1` path.

```bash
# from the Homelab checkout — source secrets once:
set -a && . ./secrets.env && set +a

# 0. Build + upload the custom unattended install ISO (one-time prerequisite —
#    the engine has no ISO fetch/build logic). Supply the base Win11 ISO:
pwsh stacks/BuildLab/scripts/build-iso.ps1 -Win11Iso <path-to-Win11.iso>

# 1. Dry-run the stack (read-only) — inspect the fresh disk/efidisk/tpmstate plan:
./build.sh Preview --stack BuildLab

# 2. Apply (creates VM 1100):
./build.sh Deploy --stack BuildLab

# 3. Start it — Windows installs unattended, then first logon runs the VS installer:
proxmoxsharp vm start desktop-01 1100
```

Re-converge after first boot should plan as **Skip** (idempotent). Detach the
install ISO once Windows is up (remove the `cdrom:` block from
[`buildvm.vm.yaml`](buildvm.vm.yaml) and re-converge).

## Unattended pipeline

The VM *shell* is IaC; the guest OS + VS installs are scripted (net-new — no prior
unattended tooling existed in the repo):

- [`unattend/autounattend.xml`](unattend/autounattend.xml) — silent Win11 install:
  GPT/UEFI partitioning, skip product key (runs **unactivated** — throwaway dev box),
  local admin, OOBE bypass (no Microsoft account), RDP enabled, virtio-scsi driver
  injection (`<DriverPaths>`), and a first-logon hook that launches the VS installer.
- [`scripts/build-iso.ps1`](scripts/build-iso.ps1) / [`scripts/build-iso.sh`](scripts/build-iso.sh) —
  bake Win11 + `virtio-win` drivers + `autounattend.xml` + the provisioner into
  `buildlab-win11-unattended.iso` and upload it to `desktop-01`'s `local` storage.
- [`unattend/provision-vs.ps1`](unattend/provision-vs.ps1) — silently installs
  **VS Community** (IDE) + **Build Tools** for 2019/2022/2026 via the Microsoft
  bootstrappers, each driven by a [`unattend/*.vsconfig`](unattend/) workload file.

### Tuning the toolchains

Workloads live in two shared files applied across all three VS versions:
[`unattend/ide.vsconfig`](unattend/ide.vsconfig) (the Community IDE) and
[`unattend/buildtools.vsconfig`](unattend/buildtools.vsconfig) (the standalone Build
Tools). Default = **minimal MSBuild / .NET**: IDE shell + MSBuild + Roslyn + .NET SDK
+ .NET Framework 4.8 SDK (IDE), and the MSBuild + managed-build workloads + .NET SDK
(Build Tools). No native/C++. Edit these to narrow/widen — they are the single tuning
point (component IDs are stable across VS 16/17/18; copy to `<year>.*.vsconfig` if a
version needs a tweak). [`provision-vs.ps1`](unattend/provision-vs.ps1) also installs
the standalone .NET SDK (channel `10.0`) so modern targets build regardless of VS version.

## Re-deployability

The VM *shell* (config, fresh disks) and the **guest install** are both reproducible:
rebuild the ISO + re-run the pipeline to get a fresh box "state of the art". Running
**unactivated + VS Community** by decision — no license keys to manage.

## Parking lot

Build agents / CI runners and other dev-test VMs can join this stack later — the
name is generic on purpose. New members take the next free VMID in 1100–1199.
