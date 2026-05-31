# Plan: BL-016 — Harden NFS into containers (host-level, storage-backed mounts)

**Issue:** [#52](https://github.com/Chrison-dev/Homelab/issues/52) (Project #7 "Homelab Backlog") ·
**Relates to:** [BL-010 Converge](iac-csharp-native.md), [BL-013 community-scripts deploy](BL-013-community-scripts-deploy.md), `CLAUDE.md` (NFS-at-host convention)
**Status:** Planned — 2026-05-31. Pattern decided; no cluster changes (CT 5007 recut is an opt-in later step).

## Problem

NFS is currently mounted **inside** some containers (notably qBittorrent / CT 5007),
which is error-prone:
- Unprivileged LXCs can't mount NFS cleanly — it needs extra caps + `nesting=1,fuse=1`.
- The service races its own in-guest mount: qBittorrent starts before the mount
  is live, writes downloads to the **unmounted path on the 10 GB `local-lvm`
  rootfs**, and fills it (the documented 5007 incident). A durable guard never landed.

## Findings (Proxmox MCP, 2026-05-31)

- **CT 5007 config carries no `mpX` and no host NFS bind** — only serial/USB device
  passthrough. With `features: nesting=1,fuse=1`, the NAS share is mounted *from
  inside the guest*. That is the fragile mechanism.
- **The host already mounts the NAS properly.** Three Proxmox NFS storages from the
  Synology (`192.168.179.11`), `shared=1`, auto-mounted at the host level:

  | Storage | Export | Host mount |
  |---|---|---|
  | `ds1813-nfs-volume-1` | `/volume1/Volume-1` | `/mnt/pve/ds1813-nfs-volume-1` |
  | `ds1813-nfs-volume-2` | `/volume2/Volume-2` | `/mnt/pve/ds1813-nfs-volume-2` |
  | `ds1813-nfs-volume-3` | `/volume3/Volume-3` | `/mnt/pve/ds1813-nfs-volume-3` |

So hardening is mostly *binding what the host already mounts* into the container,
not new plumbing.

## Decision

**Host-level NFS, never in-guest. Standard = Proxmox storage-backed `mpN`** (gated on
storage health). Confirmed with Chris 2026-05-31.

### Why (hypothesis, checked)
- **Robustness ✅** — host mounts once; the container gets a bind. No in-guest mount,
  no caps/`nesting` needed just for storage.
- **Dependency gating ✅** — a CT mountpoint *referencing the NFS storage*
  (`mpN: ds1813-nfs-volume-3:…`) is tracked by Proxmox; the CT won't start if the
  storage is offline. This is the real win over in-guest mounts.
- **Speed ⚖️** — raw throughput is ~equivalent (same host kernel NFS client either
  way). The perf upside is **FS-Cache** (`cachefilesd`/`fsc`) and a **shared page
  cache** across CTs on the same export — only possible host-side. So faster on
  re-reads / across the fleet, not magically faster for one cold read.

### The subtlety that must be respected
Storage-gating only applies to a **storage-referenced volume** (`storage:vol`), NOT a
raw path bind. Binding a host *path* that merely happens to be under `/mnt/pve/...`
re-creates the race in disguise: if NFS drops, the bind target is an empty dir and
writes hit host rootfs. Therefore:

- **App-owned data** (e.g. qBittorrent downloads, an app's working dir) → allocate a
  **volume on the NFS storage** and attach as `mpN` → fully gated. **Default.**
- **Shared pre-existing exports** (e.g. the media library the *arr stack reads) →
  there is no storage-volume to reference, so a path bind under `/mnt/pve/...` is the
  fallback. Mitigate by depending on storage activation and, where it matters, a
  pre-start `hookscript` that asserts `mountpoint -q` before start.
- **Never** mount NFS in-guest. Drop `nesting`/`fuse` where they existed only for that.

### What we are NOT doing
Auto-stopping a running CT when storage vanishes — NFS `hard` mounts *hang* rather
than error, so live auto-stop is unreliable. Posture: **gate on start + never write to
a phantom path**, plus monitoring (rootfs-usage alert), not live teardown.

## Unprivileged idmap caveat

Root-in-CT maps to uid 100000 on the host, so files written to the NAS appear as high
UIDs unless the export squashes/maps or the app aligns `PUID/PGID`. The *arr stack
already navigates this; the qBittorrent recut must match the same uid/gid scheme so the
shared library stays readable by Sonarr/Radarr.

## Shape contract impact (converge / BL-010)

`spec.mounts[].type: nfs` is hereby defined as **host-level via a Proxmox NFS storage,
rendered as a storage-backed `mpN`** — never an in-guest mount. Schema updated:
- `mount.storage` — the Proxmox NFS storage id (e.g. `ds1813-nfs-volume-3`).
- `mount.source` — for `nfs`: a volume ref or a subpath under the storage; documents
  the path-bind fallback for shared exports.
- The community-scripts create path (BL-013) still **warns-and-skips** mounts; the
  storage-backed `mpN` is applied by ProxmoxSharp converge (BL-010) or by hand.

## qBittorrent CT 5007 recut — runbook (opt-in, NOT executed)

1. **Snapshot/backup** 5007 (`vzdump` to an NFS storage).
2. **Stop** the container; record the in-guest mount (fstab/systemd unit) + qBittorrent
   save paths.
3. **Allocate a downloads volume** on the NFS storage and attach it
   (`pct set 5007 -mp0 ds1813-nfs-volume-3:<size>,mp=/downloads,backup=0`), or bind the
   shared media path for library access.
4. **Repoint qBittorrent** save/incomplete paths under the new mountpoint; align
   `PUID/PGID` with the *arr stack (idmap).
5. **Remove the in-guest NFS mount** (fstab/systemd) and drop `nesting`/`fuse` if they
   were only for that.
6. **Start**; confirm downloads land on the NAS and **rootfs usage stays flat** (the
   original failure signal). Verify the *arr stack still sees completed files.
7. **Add a rootfs-usage alert** to the monitoring stack as a backstop.

## Out of scope

- Migrating the rest of the *arr stack mounts (follow-up once the 5007 recut is proven).
- The legacy untagged `net0` on 5007 (192.168.179.x) — that's the BL-002 network migration.
- Live auto-stop on storage loss (see "What we are NOT doing").
