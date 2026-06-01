# ADR-0004 — Storage architecture: Synology as block-SAN, mergerfs gateway as the NAS

- **Status:** Accepted
- **Date:** 2026-06-01
- **Deciders:** Chris
- **Relates to:** [ADR-0001](ADR-0001-iac-tooling.md), [BL-016 NFS hardening](../plans/BL-016-nfs-hardening.md),
  build issue [#108](https://github.com/Chrison-dev/Homelab/issues/108), consumer [#105](https://github.com/Chrison-dev/Homelab/issues/105)

## Context

Containers (the *arr media stack + others) mount NFS exports off a Synology
**DS1813+** directly. We want a storage layer that is:

- **One big, easily-extendable "blob"** — add cheap mixed-size HDDs over time without
  re-wiring consumers, and not tied to one specific physical disk.
- **Abstracted** from the specific NAS — the Synology "maybe isn't the best long-term";
  we want to swap/augment it later (and possibly add other storage sources) without
  touching every container.
- **Stable** for consumers — realistically *fail-safe + fast-to-recover*, not literally
  independent (centralising storage concentrates the dependency; see Consequences).

**Hardware reality (discovered 2026-06-01 via SynoSharp + Storage Manager):**

- DS1813+ (8-bay), DSM 7.1.1, **4× 1 GbE**. Four disks, **each its own single-disk
  volume, no redundancy**: Volume-1 2 TB, Volume-2 4 TB, Volume-3 6 TB, Volume-4 8 TB
  (8 TB currently empty). **4 free bays.** ~2.8 TB live media on vols 1–3.
- Proxmox cluster: desktop-01 (Ryzen 5 3600, 6c/12t, most headroom; a gaming PC that
  **can take more NICs**), hpe-01 (i5-6500T), nuc-01 (i3); 1× 1 GbE each today.
- Explicit constraints from Chris: **no SHR** (inflexible, bad past experience); pooling
  must happen **off the Synology** so mixed/cheap disks can be added freely; iSCSI is
  acceptable; desktop-01 can gain NICs for storage bandwidth.

## Decision

**Demote the Synology to a dumb block-SAN and do all pooling + parity on a gateway VM.**

1. **Synology = block SAN.** Present **each disk as its own iSCSI LUN** (no SHR, no
   Synology-side pooling). The NAS becomes raw, swappable block storage.
2. **Gateway VM on desktop-01** (lightweight Debian, **passthrough-free** so it stays
   HA-/migration-eligible): iSCSI initiator → format each LUN (xfs) → **mergerfs** unions
   them into one expandable pool → **kernel NFS exports a single `/data`**
   (`torrents/{movies,tv}` + `media/{movies,tv}`). mergerfs is chosen because it is built
   for exactly this — mixed disk sizes, add any disk anytime (new bay → LUN →
   `mergerfs add`), and **per-disk failure isolation** (lose a disk, lose only its files).
   It is the union point for *future other storage sources* too.
3. **Parity via SnapRAID — later.** Media is re-downloadable, so start **parity-free**;
   add SnapRAID once a **≥8 TB parity disk** is bought (parity disk must be ≥ largest data
   disk). Fits the "add cheap disks over time" model.
4. **Consumers mount `/data` once, host-level + gated** (BL-016): path-bind into each CT
   at the same `/data`, pre-start `mountpoint -q` hookscript, hard NFS mount (hang-not-
   corrupt). One filesystem path → *arr hardlinks/instant-moves work, *provided* mergerfs'
   create policy keeps a title's `torrents/`+`media/` on the **same branch**.
5. **Ownership at the export, not per-app** — the community-scripts *arr apps run as root
   with no PUID/PGID, so enforce a fixed `media` uid/gid via NFS `all_squash`/anon-map +
   setgid dirs + default group-rwx ACLs.
6. **Tiering:** the mergerfs blob holds **loss-tolerant media only**. Irreplaceable data
   (app configs, *arr DBs) stays on **CT rootfs / local-lvm + PBS backups** — never on the
   unprotected blob.

## Alternatives considered

- **One big SHR volume on the Synology + direct NFS** — rejected by Chris (inflexible,
  prior bad experience) and it leaves consumers coupled to the Synology.
- **ZFS pool (local or on iSCSI)** — rejected: ZFS expansion is rigid (RAIDZ-expansion
  doesn't restripe; add-a-vdev couples disks), and ZFS *on a single iSCSI LUN* can't
  self-heal and wastes parity. Doesn't fit "throw any cheap disk at it."
- **No gateway — just consolidate + serve NFS direct** — simplest/stablest for a single
  NAS, but provides no abstraction (swapping the NAS = re-wire every container) and no
  union point, which is the stated goal.
- **TrueNAS / OpenMOMV VM** — viable (UI), but TrueNAS wants HBA passthrough (forfeits
  migration) and ZFS, neither of which fits. A plain Debian+mergerfs VM is lighter and
  scriptable (IaC-first). OMV remains an option if a UI is wanted.
- **Ceph** — wrong scale for 3 mixed nodes on 1 GbE.

## Consequences

- **+** One expandable blob; add mixed/cheap disks freely; per-disk failure isolation;
  Synology is now swappable raw storage; single `/data` abstraction for all consumers.
- **−** The gateway VM is a **deliberate, concentrated SPOF** — all consumers depend on it.
  "Stable & independent" is reframed as *fail-safe + fast-to-recover*: config-only VM
  (data on the LUNs), PBS backup + tested live-restore, HA restart-on-failure
  (passthrough-free), gated hard mounts.
- **−** **1 GbE double-hop** (container→gateway→iSCSI→Synology) ~halves throughput; fine
  for streaming, slower for bulk imports / SnapRAID sync. **Highest-value upgrade: a
  2.5/10 GbE NIC on desktop-01** (or MPIO across the NAS's 4× 1 GbE).
- **−** **Hardlinks break** if a title's download + media land on different mergerfs
  branches — the create policy + path layout must keep them co-located. Most common
  self-inflicted wound; must be tested at cutover.
- **−** Parity-free until a ≥8 TB disk is added — a disk loss = that disk's media gone
  (re-downloadable, accepted).

## Migration (sketch — full plan in #108)

Stand up the gateway VM → present the empty **8 TB (Volume-4)** as the first LUN → build
the mergerfs pool + `/data` export → verify hardlinks end-to-end on a test CT → seed-copy
the ~2.8 TB media onto the pool (originals intact) → cut over **container-by-container** →
fold the 2/4/6 TB disks in as further branches → keep PBS backups throughout. Reversible
at every step.

## Out of scope

- Maintainerr / Komodo revival (#106); the declarative *arr stack itself (#105 — consumes
  this); buying the parity disk + the NIC upgrade (operational, when convenient).
