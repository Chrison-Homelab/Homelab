# Plan: #115 — Gaming VM (Bazzite/SteamOS) on desktop-01 with GPU passthrough

**Issue:** [#115](https://github.com/Chrison-dev/Homelab/issues/115) ·
**Relates to:** [#57 SynoSharp write path](057-synosharp-write-path.md) (the layered, dry-run-default
pattern this mirrors), [ADR-0001 shape contract](../adr/), [#113 converge apply](BL-010-converge.md)
**Status (2026-06-15):** Phases **A–D DONE + L4 DONE**. Both `bazzite` (1003) and the adopted
Windows VM `gaming-vm-01` (1002) hold the Radeon via `hostpci0: mapping=AMD_Radeon_RX6600`,
applied through the hub: **`homelab-infra converge stacks/Gaming [--apply]`** loads the `*.vm.yaml`,
maps it to ProxmoxSharp's `QemuVmSpec`, and reconciles idempotently (re-plan = Skip). 46 hub tests
green. **Phase D done** — 1002 boots on the GPU running Satisfactory, streamed to the MacBook via
**Steam Remote Play** (Sunshine/Moonlight evaluated, not needed). Remaining = only **Phase E**
(promote the stack to a submodule).

**Decisions (2026-06-08):** desktop-01 will become the **dedicated gaming node** (LXCs migrate
off) → the 16 GB pressure resolves, VM keeps/raises its 12 GB. One gaming VM at a time is fine
(single player). OS side is **throwaway** — reproduce the *machine* via IaC; OS is reinstalled
"state of the art" when needed (backups best-effort, low priority).

## Goal

A re-deployable, **IaC-managed Bazzite (SteamOS-style) gaming VM** on `desktop-01`, with the
discrete GPU at `0000:09:00` passed through, streamable to a MacBook. Dual purpose:

1. **Play** PC games on the MacBook via streaming (Proton/Steam gaming mode).
2. **Test ErpForFactoryGames** against real games in a real GPU + Proton environment.

Bazzite is the pragmatic "SteamOS" for a VM (Fedora-atomic, SteamOS-style gaming mode + Steam +
Proton, good passthrough/Sunshine support) — and it's already what the existing `gaming-vm-02`
runs. True SteamOS 3 / HoloISO is Steam-Deck-hardware-targeted and fiddly in a VM; rejected.

## Confirmed by live probe (2026-06-08, read-only)

**desktop-01** — Ryzen 5 3600 (6c/12t, **no integrated GPU**), **16 GB RAM**, PVE 9.2.2,
kernel 7.0.2-6-pve. One discrete GPU at PCI `0000:09:00`.

| VMID | Name | What it tells us |
| --- | --- | --- |
| 1002 | gaming-vm-01 (Win11) | **Working passthrough**: `hostpci0: 0000:09:00,pcie=1,x-vga=1`, q35/OVMF/TPM, 6c/12GB/120GB. → host VFIO is already configured and proven. |
| 1003 | gaming-vm-02 (Bazzite) | Existing Bazzite VM (`bazzite-deck-stable-live.iso`), q35/OVMF/6c/12GB/120GB, **no `hostpci`** → never got the GPU. **We adopt this one** and add passthrough. |
| 1001 | Plex-VM | Gaming-adjacent, stays **unmanaged**. |

Reference config (1002, the proven passthrough recipe we clone):

```
machine pc-q35-10.1 · bios ovmf · cpu host · cores 6 · memory 12288
scsihw virtio-scsi-single · scsi0 local-lvm:...,iothread=1,size=120G,ssd=1
efidisk0 local-lvm:...,efitype=4m,pre-enrolled-keys=1 · agent 1
hostpci0 0000:09:00,pcie=1,x-vga=1            ← the GPU passthrough line
```

## The three workstreams

A VM is **greenfield on all three layers** today — that's where the work is.

### 1. Schema — tighten `kind: VM`

`shape.schema.json` already lists `VM` in the kind enum but leaves `spec` **permissive**
("permissive until their contracts are tightened"). Author a strict `vmSpec` (sibling to
`lxcSpec`), covering the QEMU surface we actually use:

- core: `node`, `vmid`, `name`, `machine` (q35), `bios` (ovmf), `cpu` (host), `cores`,
  `sockets`, `memory`, `numa`, `ostype`, `agent`, `onboot`, `startup`, `tags`
- storage: `scsihw`, `disks[]` (id/storage/size/ssd/iothread), `efidisk`, `tpmstate` (opt)
- network: reuse the existing `network`/`networkInterface` `$defs`
- install: `cdrom`/`iso` (Bazzite ISO for first boot)
- **`hostpci[]`** — the novel field: `{ id: "0000:09:00", pcie: true, xVga: true, rombar?, mdev? }`.
  This is the heart of the gaming shape and has no LXC analog.
- add the `if kind == VM` branch in `allOf` (mirroring LXC/Stack); update `SchemaDriftTests`.

### 2. ProxmoxSharp VM write path

Extend the read-only CLI (`discover`/`nodes`/`version`) into a layered reconciler — **easier
than SynoSharp** because Proxmox has a published schema and the Kiota-generated Qemu request
builders already exist in `vendor/ProxmoxSharp`:

```
L1  Qemu wrappers   typed create / setConfig / start / stop / status   (over generated client)
                    + PCI listing for passthrough validation (IOMMU group / device exists)
L2  Reconciler      diff vmSpec desired-state vs the discover snapshot → Plan { actions }
L3  Apply           proxmoxsharp vm plan / vm apply --confirm           DRY-RUN BY DEFAULT
L4  Hub / renderer  kind: VM shape → reconciler (the VM analog of Deploy-Shape.ps1's LXC path)
```

Same `--dry-run`-by-default, read-before-write safety as `Deploy-Shape.ps1` and SynoSharp.
Adoption-friendly: reconciling against an **existing** VM (1003) must emit only the *delta*
(add `hostpci0`), never recreate.

### 3. Gaming stack + shape

- `stacks/Gaming/stack.yaml` — `kind: Stack`, **VMID block 1000–1099**, defaults (node
  desktop-01, machine q35, bios ovmf, cpu host, timezone). 1001/1002 fall in-range but are
  **explicitly unmanaged** (same precedent as CT 2005 in DevOps).
- `stacks/Gaming/bazzite.vm.yaml` — `kind: VM`, **vmid 1003 (adopted)**, clones the 1002
  passthrough recipe (`hostpci0: 0000:09:00,pcie=1,x-vga=1`).
- Scaffolded **locally** for now (not yet a submodule repo) — promote to
  `Chrison-dev/Homelab.Stacks.Gaming` when the shape stabilises.

## Streaming to the MacBook

- **Steam Remote Play** (guest Steam ↔ Mac Steam) — **the chosen path**: zero-config,
  survives the throwaway-OS reinstall. In production for 1002 (Satisfactory).
- **Sunshine** in the guest + **Moonlight** on the Mac (low-latency, NVENC/AMF) — lower-latency
  alternative; evaluated, not needed.
- Guest-side config; not modeled as a separate stack member.

## Constraints / caveats (be honest)

- **One *gaming* GPU, one VM.** Two GPUs exist (Radeon RX 6600 @ `0000:09:00`, Quadro P400 @
  `0000:06:00`), but only the Radeon is gaming-capable — so the SteamOS VM and the Windows VM (1002)
  remain mutually exclusive on it. **Accepted** (single player). The host keeps the P400, so it is
  **not** headless. No host reconfig needed (1002 proves VFIO works); the reconciler should still
  warn if another VM holds the same `hostpci`.
- **RAM — resolved by making desktop-01 gaming-only.** LXCs migrate off (separate effort), so the
  16 GB no longer contends with the 8 stacks; the VM keeps 12 GB (or more once it's the sole load).
- **Re-deployability has a manual seam — accepted.** The VM *shell* (config, disks, passthrough)
  is fully IaC and idempotent. The **guest OS install + Steam login is one-time manual**, and
  that's fine: we reinstall "state of the art" when needed rather than chasing OS backups.
  Optional later: a post-install snapshot as the redeploy base.

## Phased scope

- **Phase A — schema. ✅ DONE (2026-06-08).** Authored `vmSpec` + the novel `hostpci[]` def
  (+ `vmDisk`/`vmEfiDisk`/`vmTpmState`/`vmCdrom`/`vmBoot`), added the `kind: VM` `allOf` branch
  (requires `vmid`), mirrored `VmSpec` C# model, added drift tripwire (`vmSpec`/`hostPci`/`vmDisk`)
  + positive/negative validator tests. 29 tests green; `bazzite.vm.yaml` + `stack.yaml` validate.
  *(No infra touched.)*
- **Phase B — ProxmoxSharp write path.** *(decisions 2026-06-08: regenerate the client with write
  verbs; full VM lifecycle.)*
  - **✅ Regen done.** `ProxmoxSharp.Api.csproj` now drives codegen with `--methods GET,POST,PUT,DELETE`
    (new `ApiMethods` property; scope unchanged at `/version,/nodes,/cluster,/storage,/access`).
    kiota is a pinned local tool (`dotnet tool restore`) — no global install. Verified: `qemu`
    POST(create) / `config` PUT+POST / `…/{vmid}` DELETE / `status/{start,stop,shutdown}` POST all
    present; read path + all tests still green (ProxmoxSharp 6, hub 29).
  - **L1 serialization note:** kiota collapses Proxmox indexed device params to single placeholders
    (`Hostpcin`/`Scsin`/`Netn`/`Iden`, plus fixed `Efidisk0`) — so L1 must map indexed devices to
    concrete keys (`hostpci0`, `scsi0`, `net0`) itself. Scalars are clean typed query params.
  - **✅ L1–L3 done + read-only live-validated (2026-06-08).** `QemuWriter` (L1: create/setConfig/
    start/stop/shutdown/delete via form-body over the shared adapter + task polling + pci/config
    reads), `QemuVmSpec`+`QemuParamEncoder`+`VmReconciler` (L2: subset-diff), CLI `vm plan|apply|
    start|stop|delete|pci|show` (L3: `--confirm` to mutate, dry-run default). 25 ProxmoxSharp tests
    green (+19). **`vm plan` against the LIVE 1003** correctly produced just `+ hostpci0` and
    `~ name: gaming-vm-02→bazzite`, leaving all Proxmox-added keys (boot/ide2/numa/smbios1/…)
    untouched — the subset semantics hold against real config.
  - **GPU correction (live `vm pci`):** desktop-01 has **two** GPUs — `0000:09:00` AMD Radeon RX 6600
    (iommu 18/19, VGA+audio; the passthrough target — whole-device id passes both functions) AND
    `0000:06:00` NVIDIA Quadro P400 (iommu 15). So the host can keep the P400 while the Radeon
    drives the gaming VM — the "host goes headless" caveat is **softened** (host keeps a display).
  - **✅ Live-verify done (2026-06-08).** Throwaway VM 9990 (seabios, 1 GB, 512 MB, no GPU) taken
    through create→start→stop→delete-purge — every Proxmox task returned OK, VM + disk fully removed,
    no real VM touched. The write path is proven against the live API.
- **Phase C — adopt + passthrough. ✅ DONE (2026-06-13).** VM 1003 renamed `gaming-vm-02 → bazzite`
  and the Radeon attached: `hostpci0: mapping=AMD_Radeon_RX6600,pcie=1,x-vga=1`. Re-plan is idempotent.
  - **Key finding:** Proxmox refuses a *raw* `hostpciN` from an API token ("only root can set
    hostpciN config for non-mapped devices", HTTP 500). The token-settable + node-portable way is a
    **PCI resource mapping** (we already map the Quadro P400). Created `AMD_Radeon_RX6600` →
    `0000:09:00` (id 1002:73ff, iommu 18) at `/cluster/mapping/pci` (token has `Mapping.Modify`);
    attaching it needs only `Mapping.Use`. ProxmoxSharp `hostpci` gained a `mapping=` form
    (PR ProxmoxSharp#15) and the `kind: VM` schema/shape now model `mapping` (preferred) vs raw `id`.
  - **Still to do:** boot 1003 into Bazzite on the Radeon (start = claims the GPU; host keeps the P400).
- **Phase D — streaming + redeploy base. ✅ DONE (2026-06-15).** 1002 boots on the Radeon running
  Satisfactory, streamed to the MacBook via **Steam Remote Play** (zero-config, survives the OS
  reinstall) — Sunshine/Moonlight evaluated but not needed. Adopting 1002 also surfaced the
  **raw→mapping root seam**: a scoped token can't rewrite *or delete* a root-set raw `hostpciN`
  ("only root can set hostpciN config for non-mapped devices"), so a one-time root
  `qm set 1002 --delete hostpci0` was needed before the token-driven converge added the mapping
  fresh. The engine now sequences this as delete-then-set (`VmConverger.RawToMappingTransitions`),
  though clearing the raw entry stays root-gated for a minimal token. Redeploy-base snapshot
  remains optional/deferred (OS is throwaway).
- **Phase E — promote stack** to its own submodule repo once stable.

## Out of scope (deliberately)

- Touching VMs 1001 / 1002 (unmanaged).
- Host VFIO / IOMMU reconfiguration (already working via 1002).
- Fully unattended guest OS install (Phase D best-effort; manual install accepted for v1).
- True SteamOS 3 / HoloISO.
