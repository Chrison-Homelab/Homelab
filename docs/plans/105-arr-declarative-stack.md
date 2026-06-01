# Plan: #105 — Declarative *arr media stack (rebuild as IaC, NFS-hardened)

**Issue:** [#105](https://github.com/Chrison-dev/Homelab/issues/105) ·
**Relates to:** [#43 shapes](iac-csharp-native.md), [BL-013 community-scripts deploy](BL-013-community-scripts-deploy.md),
[#45 converge](BL-010-converge.md), [BL-016 NFS hardening](BL-016-nfs-hardening.md) ·
**Defers to:** [#106 Komodo revival + Maintainerr](https://github.com/Chrison-dev/Homelab/issues/106) ·
**Status:** Scoping — 2026-06-01.

## Goal

Rebuild the entire *arr media stack as **declarative `homelab/v1` shapes** (a new
`stacks/Media` submodule), deployed via community-scripts over SSH — reproducible,
**redeployable**, and easy to tweak. Replaces today's hand-built CTs (incl. the
qBittorrent CT that caused the rootfs-fill NFS race). **Plex stays as-is** and keeps
reading the library.

## Current state (discovered 2026-06-01)

The *arr apps **already run as live CTs** in the ~5000s block (`prowlarr, sonarr,
radarr, bazarr, seerr, qbittorrent`, plus `flaresolverr`, `tracearr`, a `plex` CT and
a `Plex-VM`) — but **none are captured in the IaC repo**. So #105 is greenfield *IaC*,
not greenfield *apps*. There is no `Media`/`Arr` stack submodule yet.

NFS today: Proxmox has `ds1813-nfs-volume-{1,2,3}` (Synology `192.168.179.11`). The
current library lives on **volume3** (~radarr 1 TB, sonarr 1.8 TB). **volume4 (8 TB,
unused) is NOT wired** — no Synology export, no Proxmox storage yet.

community-scripts caveat: the *arr installers are **native Debian/systemd**, run **as
root**, config under `/var/lib/<app>/`. There is **no PUID/PGID/UMASK** model (that's
the LinuxServer Docker convention). Ownership must therefore be enforced **at the NFS
export**, not per-app.

## The load-bearing constraint — ONE shared `/data` export (hardlinks)

Per TRaSH-Guides, downloads and the library must be on the **same filesystem** so *arr
can **hardlink + instant-move** (otherwise: copy+delete → 2× disk + slow). With each app
in its own LXC this means:

> **Every file-touching CT (sonarr, radarr, bazarr, qbittorrent) mounts the *same single*
> NFS export at the *same path* `/data`.** `torrents/` and `media/` are **subfolders** of
> that one mount — never split into separate mounts.

```
/data                       <- one NFS export, identical mount in every *arr CT
├── torrents/{movies,tv}     <- qBittorrent writes here
└── media/{movies,tv}        <- *arr import here; Plex reads here
```

This is **BL-016's shared-export branch**, not the storage-backed-volume branch (a
shared library can't be a single-CT `mpN`). So `/data` is a **path-bind** of the volume4
export into each CT, gated by a **pre-start `mountpoint -q` hookscript** + storage
activation (so a dropped NAS never lets writes hit the rootfs — the 5007 failure mode).

## volume4 layout + ownership (prerequisite)

1. **Synology (manual / DSM — SynoSharp is read-only):** create a shared NFS export on
   volume4, e.g. `/volume4/data`, containing `torrents/{movies,tv}` + `media/{movies,tv}`.
   - Ownership enforced here: a fixed `media` gid + service uid; **`all_squash` with
     `anonuid`/`anongid`** mapping every client write to that identity (neutralises the
     "CS apps run as root" problem); **setgid dirs + default group-rwx ACLs** (umask-002
     equivalent) so new files stay group-writable.
2. **Proxmox:** add it as NFS storage `ds1813-nfs-volume-4` (host mount
   `/mnt/pve/ds1813-nfs-volume-4`).
3. **Each *arr CT:** path-bind `/mnt/pve/ds1813-nfs-volume-4/data` → `/data` (+ hookscript
   guard). Plex (unchanged) mounts the same export read-side at `/data/media`.

> **Future-proofing** (the "Synology → SAN, NAS-layer abstraction" idea, separate task):
> apps only know `/data`. Whatever serves it — Synology-direct NFS today, a NAS-VM
> re-export later — is swappable without touching the stack.

## Stack shape — `stacks/Media` (fresh 5100 block, parallel build)

Build alongside the live 5000s CTs, validate, migrate, then retire the old.

| CTID | App | Notes |
|---|---|---|
| 5100 | prowlarr | indexer hub; **migrate existing indexer DB** |
| 5101 | sonarr | `/data` mount |
| 5102 | radarr | `/data` mount |
| 5103 | bazarr | `/data` mount (subtitles) |
| 5104 | qbittorrent | `/data` mount; categories `tv-sonarr`/`radarr` |
| 5105 | seerr | request UI (unified Overseerr+Jellyseerr successor; Plex-native) |
| 5106 | *(reserved)* | 2nd torrent client if needed — **Deluge** (CS-available, light) |
| 5107 | flaresolverr | Cloudflare-challenge solver for Prowlarr indexers |

Cleanup automation (**Maintainerr**, Docker-only) is **NOT** here — deferred to #106
(revive Komodo first).

## Cutover / migration

- **Indexers:** back up + restore Prowlarr's `/var/lib/prowlarr` config DB into the new
  5100 Prowlarr → indexers carry over and re-sync to Sonarr/Radarr.
- **Quality profiles:** start **fresh** via **Recyclarr** (TRaSH profiles as code) — no
  need to preserve the old ones.
- **Libraries:** **curate down to the current watchlist** (don't bulk-copy ~2.8 TB);
  re-point/import the kept titles under `/data/media` on volume4. Needs a human curation
  pass.
- Retire the old 5000s CTs once the new stack is verified end-to-end.

## Open items / prerequisites

1. **volume4 export** must be created on the Synology with the squash/ownership model
   above (manual DSM step — can be scripted into a runbook). Blocks the data move.
2. **`spec.mounts` for a shared path-bind** — confirm the shape model expresses a
   shared-export `type: nfs` path-bind (vs the Forgejo storage-volume case); may need a
   `source` subpath + the hookscript wiring (converge #101 territory).
3. Library curation is a manual/judgement pass (what's still on the watchlist).
4. PUID/PGID-free ownership relies on the NFS export squash + ACLs — validate writes from
   two CTs land as the same uid/gid and hardlink across `torrents`↔`media`.

## Out of scope (separate tasks)

- Maintainerr + Komodo docker-stack revival → **#106**.
- Storage-layer abstraction (NAS-VM fronting the Synology as raw storage) → future.
- Plex (CT + Plex-VM) — left as-is.
