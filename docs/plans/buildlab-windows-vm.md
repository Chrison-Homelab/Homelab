# Plan: BuildLab — Windows 11 dev/build VM (IaC)

**Issue:** [#233](https://github.com/Chrison-dev/Homelab/issues/233).
**Relates to:** [#115 Gaming VM](115-gaming-vm-steamos.md) (the `kind: VM` shape + ProxmoxSharp
write path this reuses), [ADR-0001 shape contract].
**Status (2026-07-01):** Phase A **DONE** (stack + greenfield VM shape, validates). Phases B–C
**authored** (unattended Windows + VS install pipeline) — pending a live build/deploy on a box with
`secrets.env` + the GitHub Packages token. Phase D pending that deploy.

## Context

Chris needs a throwaway Windows dev box to test the **Fallout .NET build framework**
(`Fallout.Build`/`Fallout.Common`/`Fallout.Components` — the Nuke-style build system this repo's
`build.sh` runs on) across multiple Visual Studio MSBuild toolchains. Requirement: Windows 11 + **VS
2019, 2022 and 2026**, both **Community IDE** and **Build Tools** for each. Dedicated stack (build
agents / other dev VMs may join later), reproducible via IaC like the rest of the lab.

The repo already has a proven Windows-on-Proxmox IaC pattern — VM 1002 (`gaming-vm-01`) is a working
Win11 guest declared as `stacks/Gaming/windows.vm.yaml`, reconciled by the `homelab-infra` engine via
the ProxmoxSharp VM write path. BuildLab clones that recipe **minus GPU passthrough, plus a greenfield
disk and an unattended install pipeline** (net-new — the gaming VM's OS was installed by hand).

### Decisions (with Chris, 2026-07-01)
- **Node / sizing:** `desktop-01` (strongest CPU, hosts the proven Win11 recipe), **start-on-demand**
  (`onboot: false`) at **12 GB / 6 cores** — borrows RAM only while testing; coexists with the
  (also on-demand) gaming VMs. All three nodes are 16 GB, so this is the only sane fit.
- **Automation:** **Fully unattended** — `autounattend.xml` + a scripted silent VS install.
- **Licensing:** **Unactivated Windows + VS Community** — zero key wrangling (throwaway dev box).
- **Stack:** **BuildLab**, new VMID block **1100–1199**; VM **1100** = `buildvm`.
- **Workloads:** Fallout is a **.NET build framework** → **minimal MSBuild/.NET** (no native/C++).
  Disk cut to **150 GB thin** accordingly (6 lean .NET-build installs are nowhere near a C++/full-IDE box).

## What shipped

A self-contained local stack at `stacks/BuildLab/` (promote to a submodule later, per the Gaming precedent):

| File | Purpose |
|---|---|
| `stack.yaml` | `kind: Stack`, VMID block 1100–1199, defaults (node desktop-01, tz, tags). |
| `buildvm.vm.yaml` | `kind: VM` 1100 — q35/OVMF/vTPM 2.0, **greenfield** 150 GB disk + fresh efidisk/tpmstate, no hostpci, on-demand 12 GB. |
| `README.md` | Stack docs (house template). |
| `unattend/autounattend.xml` | Silent Win11: UEFI/GPT partition, skip key (KMS client key → unactivated), local admin, OOBE bypass, RDP on, virtio-scsi `<DriverPaths>`, first-logon hook → the VS provisioner. |
| `unattend/provision-vs.ps1` | Silently installs VS 2019/2022/2026 Community + Build Tools via the MS bootstrappers; idempotent (vswhere skip); also installs the standalone .NET SDK; disables auto-logon when done. |
| `unattend/{ide,buildtools}.vsconfig` | Minimal .NET workload sets (shared across versions; the single tuning point). |
| `scripts/build-iso.ps1` / `.sh` | Bake Win11 + virtio drivers + autounattend + the guest payload (`sources\$OEM$` → `C:\BuildLab`) into `buildlab-win11-unattended.iso`; upload to desktop-01 `local` storage. |

## How it works (deploy flow)

1. `set -a && . ./secrets.env && set +a` (Proxmox creds + GitHub Packages token).
2. **Bake the ISO** (one-time prerequisite — the engine has no ISO build logic):
   `pwsh stacks/BuildLab/scripts/build-iso.ps1 -Win11Iso <path>` → uploads to `desktop-01`.
3. **Dry-run:** `./build.sh Preview --stack BuildLab` — inspect the create plan, especially the
   **fresh** disk/efidisk/tpmstate encoding (the risk below).
4. **Apply:** `./build.sh Deploy --stack BuildLab` → creates VM 1100.
5. `proxmoxsharp vm start desktop-01 1100` → Windows installs unattended → first logon auto-runs
   `provision-vs.ps1` → all 6 VS installs + the .NET SDK.
6. **Re-converge = Skip** (idempotent). Post-boot: detach the install ISO (remove `cdrom:` and
   re-converge), add a UniFi DHCP reservation, RDP in, `vswhere -all` confirms 2019/2022/2026.

## Verification

- **Schema (done):** `homelab-infra validate stacks/BuildLab` → `2/2 shape(s) valid`. Full run is
  `./build.sh` (ValidateShapes) + `dotnet test Infrastructure/engine.Tests` (SchemaDriftTests) — no
  schema change made, so drift tests are unaffected.
- **Plan correctness:** `./build.sh Preview --stack BuildLab` shows a clean create, correct fresh
  disk/efidisk/tpmstate params, **no `hostpci`**.
- **End-to-end:** apply → start → unattended install completes → `vswhere -all` shows the 6 installs →
  re-converge = Skip.
- **Free space:** confirm desktop-01 `local-lvm` has room for a 150 GB thin volume before apply.

## Constraints / caveats (honest)

- **Cannot build/deploy from this dev checkout.** `./build.sh` needs the **GitHub Packages token**
  (the Fallout build packages 401 without it) and converge needs **`secrets.env`** (Proxmox creds) —
  neither is present here. Validation works offline (pre-built engine binary). Run B–D on a box that
  has both.
- **Fresh OVMF+TPM create path — encoder confirmed, apply still to be live-checked.**
  `QemuParamEncoder` *does* handle greenfield allocation (verified in source): no-`source` disk →
  `local-lvm:150`, efidisk → `local-lvm:1,efitype=4m,pre-enrolled-keys=1`, tpmstate →
  `local-lvm:1,version=v2.0`. The gaming VMs only ever *adopt*, so this exact path hasn't run live —
  confirm the `Preview` emits those params and that Proxmox accepts the fresh alloc on `Deploy`. (Also
  confirm the referenced ProxmoxSharp **package** version, `0.2.0-preview.31`, carries this encoder
  logic — the vendored source does.)
- **autounattend.xml needs your ISO's specifics:** the install image name (`/IMAGE/NAME` →
  `dism /Get-WimInfo`), and the WinPE driver-path drive letter (we list D:/E: candidates). Flagged inline.
- **RAM is tight** (12 GB on-demand on a 16 GB node) — gaming VMs must be off while this runs. Raise
  once desktop-01 sheds LXCs.
- **First-boot manual seam** (accepted, same as the gaming VM): supply the base Win11 ISO once, detach
  install media post-install, add the DHCP reservation. Everything else is scripted/idempotent.

## Out of scope (deliberately)

- Build agents / CI runners on this VM (future — stack named generically to allow it).
- Windows/VS activation (unactivated + Community by decision).
- GPU passthrough (headless build box).
- Promoting BuildLab to its own submodule repo (later).

## Verification — build-and-verify on desktop-01 (2026-07-18)

Driven through a real build via `homelab-infra Deploy --stack BuildLab` on desktop-01
(VMID 1100), using the on-node `Win11_24H2_EnglishInternational_x64.iso` (official MS
retail image) + the node's `virtio-win.iso`.

**Verified working:** the full **unattended Windows 11 install** — boots the custom
ISO, auto-skips every setup screen (edition / disk / OOBE), lands on an
auto-logged-in desktop (confirmed via QEMU screendumps).

**Three defects found — two fixed here, one fixed-pending-revalidation:**

1. **Boot image — FIXED, validated.** `build-iso.{sh,ps1}` baked the *prompting* UEFI
   boot image (`efisys.bin`) → the unattended boot hung at "Press any key to boot from
   CD" → PXE fallback → loop. Now uses `efisys_noprompt.bin` (both scripts); boots
   straight into Setup.

2. **Answer-file language must match the base ISO — media dependency.** The WinPE
   `UILanguage` is `en-US`; on an **EnglishInternational (en-GB)** ISO, 24H2's new
   setup can't satisfy the language pass and falls back to the "Select language
   settings" prompt (hangs the unattended flow). `en-US` is correct for an **en-US**
   base ISO, so this is a build-time requirement, not an answer-file bug: **set the
   WinPE `UILanguage` to match the base Win11 ISO's language** (en-GB for the ISO used
   above). Left en-US here (the en-US-media design); flagged for build-iso callers.

3. **`$OEM$` payload staging — FIXED here, re-validation pending.** `provision-vs.ps1`
   + guest tools are staged via `sources\$OEM$\$1\BuildLab`, which Setup only processes
   when the answer file sets `<UseConfigurationSet>true</UseConfigurationSet>`
   (Microsoft-Windows-Setup, windowsPE). It was missing → `C:\BuildLab` came up empty
   → both FirstLogonCommands (guest-tools install + VS provisioner) silently no-op'd →
   **no VS installed** (the whole point of the VM). Added `UseConfigurationSet`. **Still
   to re-validate:** VS 2019/2022/2026 provisioning completing + a Fallout build
   succeeding on the box.

**Net:** not merge-ready until the VS-provisioning path is re-run and confirmed with
fix #3; the install pipeline itself is proven.

## Delivery rework (2026-07-18) — $OEM$ replaced by ISO-root + copy-from-CD

Second build-verify run confirmed that Win11 24H2's reimagined setup does **not** stage
the `sources\$OEM$` tree — `C:\BuildLab` came up empty both without and with
`<UseConfigurationSet>` (verified in-guest: `dir c:\buildlab` -> File Not Found), so the
FirstLogonCommands silently no-op'd and no VS was installed. `UseConfigurationSet` was the
wrong remedy (it points setup at a config-set `$OEM$`, not `\sources\$OEM$`).

Reworked to avoid `$OEM$` entirely:
- `build-iso.{sh,ps1}` now stage the payload at the **ISO ROOT** (`\BuildLab`) instead of
  `sources\$OEM$\$1\BuildLab`.
- `autounattend.xml` drops `UseConfigurationSet` and adds a first-logon `Order 1` that
  copies `\BuildLab` off the still-mounted install CD to `C:\BuildLab` (`for %d in ... xcopy`),
  before the guest-tools install (Order 2) and the VS provisioner (Order 3). FirstLogonCommands
  are inline in the answer file (not `$OEM$`-delivered), so they run reliably.

**Still to re-validate end-to-end:** a fresh install with this rework — confirm `C:\BuildLab`
populates, the guest agent installs, VS 2019/2022/2026 provision, and a Fallout build succeeds.
(The provisioner logic itself is being validated separately by running provision-vs.ps1 by hand.)
