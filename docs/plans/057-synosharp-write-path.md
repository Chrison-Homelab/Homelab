# Plan: #57 — SynoSharp write path (SSH-runner → reconciler)

**Issue:** [#57](https://github.com/Chrison-dev/Homelab/issues/57) ·
**Relates to:** [ADR-0002 (SynoSharp SSH-runner)](../adr/ADR-0002-synosharp.md),
[iac-csharp-native plan](iac-csharp-native.md), [BL-013 deploy renderer](BL-013-community-scripts-deploy.md) (the dry-run pattern this mirrors)
**Status:** Scoped — 2026-05-31. Transport wedge done + live-verified
([SynoSharp PR #3](https://github.com/Chrison-dev/SynoSharp/pull/3)). Phase A next.

## Goal

Bring the Synology NAS (DS1813+, DSM 7.1.1-42962) under declarative IaC the way
ProxmoxSharp did for Proxmox: a desired-state spec is diffed against live state and
only the needed changes are applied, **dry-run by default**. The transport is SSH
(ADR-0002: no published schema → no codegen), but the *ergonomics* mirror the
siblings (a library + `synosharp` CLI) — so callers never hand-write raw `syno*` argv.

## Confirmed by live probe (2026-05-31, read-only over SSH)

Root SSH can change practically everything on DSM, but the on-box tools split by how
*wrappable* they are — and **none are idempotent** (`synoshare --add` on an existing
share errors), which is the whole reason a reconciler layer is needed.

| Domain | Tool(s) | Surface | Tier |
| --- | --- | --- | --- |
| Users | `synouser` | `--add/--modify/--del/--rename/--setpw/--enum/--get` | ✅ 1 clean CRUD |
| Groups | `synogroup` | `--add/--del/--rename/--member/--memberadd/--enum` | ✅ 1 clean CRUD |
| Shares + ACLs | `synoshare` | `--add/--del/--rename/--setuser/--setdesc/--get/--enum/--list_acl` | ✅ 1 (`--add` = 8 positional args) |
| Packages | `synopkgctl` | `enable/disable/start/stop/setup/teardown` | ✅ 1 lifecycle (not install) |
| Network | `synonet` | `--manual/--dhcp/--set_gateway/--set_dns/--set_mtu/--set_hostname/--vlan_*/--la_*` | ⚠️ 1 clean but high blast radius |
| NFS exports | `synonfs` + `synowebapi` | `synonfs` is only a helper (`--check-affected`/`--check-pause`); real config via `synowebapi SYNO.Core.Share set` w/ `nfs_privilege` | 🔶 2 fragile/undocumented |
| Firewall | `synofirewall` | `--enable/--disable/--profile-set/--enum/--export`; rule CRUD via profiles/webapi | 🔶 2 |
| Scheduled tasks | `synoschedtask` | `--get/--del/--run` only — **no `--add`** (create via webapi) | 🔶 2 |
| Storage / RAID / volumes | `synostgpool`, `synoraidtool`, `synostorage` | exists | ⛔ 3 don't automate |
| DSM updates | `synoupgrade` | exists | ⛔ 3 manual |

- `synoshare --add sharename desc path na rw ro browsable{0|1} adv_privilege{0~7}`
  is the canonical "cryptic positional argv" case the L1 wrappers encode once.
- `synowebapi --exec api=SYNO.Core.Share method=get` returns `success:true` over our
  runner → the Tier-2 webapi path is *available*, just undocumented + version-pinned.
- All `syno*` CLIs are **root-only even for `--help`** → the runner's sudo path
  (PR #3) is mandatory. `homelab` is in the `administrators` group, so sudo works.

## Architecture — layered (mirrors ProxmoxSharp + the BL-013 dry-run pattern)

```
L0  Transport      ISshRunner / SshRunner              DONE (PR #3) — raw argv over SSH, sudo via stdin
L1  Tool wrappers  SynoShare · SynoUser · SynoGroup     imperative typed methods →
                   (· SynoNet · SynoPkg · SynoNfs)      build SynologyCommand + parse output
L2  Resource model ShareSpec · UserSpec · GroupSpec…    declarative desired-state records
    + Reconciler   Reconcile(desired, discovered) →     diff → Plan { PlannedAction[] }
                                                         each action carries its SynologyCommand + a description
L3  Apply          Apply(plan, dryRun: true)            DRY-RUN DEFAULT; read-before-write
L4  Engine/shapes  YAML desired-state → homelab-infra   ties into the hub, like Proxmox shapes
```

The **reconciler (L2) is the heart**: it diffs desired state against the discover
snapshot we already build (shares/users are live-verified), so the non-idempotent
`syno*` tools become safe — only `create`/`modify`/`delete`/`skip` actions are emitted.
L1 encodes the positional-argv knowledge once per tool. L3 gives the same
`--dry-run`-by-default safety as `Deploy-Shape.ps1` — nothing mutates without `--apply`.

## The one real risk: NFS

NFS exports are the **highest homelab value** (the Proxmox hosts NFS-mount this NAS —
see [BL-016 NFS hardening](BL-016-nfs-hardening.md)) but the **weakest path**:
`synonfs` cannot create exports, so it's `synowebapi SYNO.Core.Share set` with an
undocumented `nfs_privilege` structure, pinned to DSM 7.1. Per ADR-0002 it comes
**last** and should be proven on the Virtual DSM container — which needs an x86/KVM
host we don't currently have (it won't run on Apple Silicon).

## Phased scope

- **Phase A** — L1+L2+L3 for **shares + users + groups** (all Tier 1, all already in
  the discover snapshot → easy reconciler, dry-run default). High value, low risk.
  - `EnsureShareAsync` / `EnsureUserAsync` / `EnsureGroupAsync` (or spec lists →
    `Reconcile` → `Plan` → `Apply(dryRun)`).
  - `synosharp plan <spec.yaml>` (show the diff) and `synosharp apply --confirm`.
  - Live-verify reversibly: create a throwaway `synosharp-test` share, then delete it.
- **Phase B** — fold into the hub (L4 YAML shapes) so DSM state is declarative like Proxmox.
- **Phase C** — NFS via `synowebapi`, last, on Virtual DSM (needs an x86/KVM host).
- Network/firewall: wrap **read-only first**; gate writes hard (blast radius).

## Out of scope (deliberately)

Tier 3 — storage pools, RAID, volume creation, DSM updates. Destructive, one-time,
and rare; left as manual UI operations with no automation.
