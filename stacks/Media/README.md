# Media stack — declarative *arr (issue #105)

Rebuild of the *arr media stack as declarative `homelab/v1` shapes — reproducible,
redeployable, NFS-hardened. Built as a **fresh 5100 CTID block in parallel** with
the live hand-built 5000s CTs, then cut over and retired. **Plex stays as-is** and
keeps reading the library.

Plan: [`docs/plans/105-arr-declarative-stack.md`](../../docs/plans/105-arr-declarative-stack.md) ·
Prerequisite runbook: [`docs/runbooks/volume4-data-export.md`](../../docs/runbooks/volume4-data-export.md)

## The load-bearing constraint — one shared `/data`

Per TRaSH-Guides, downloads and the library must be on the **same filesystem** so
the *arr apps **hardlink + instant-move** (otherwise copy+delete → 2× disk, slow).
With each app in its own LXC, that means **every file-touching CT binds the *same
single* NFS export at the *same path* `/data`** — `torrents/` and `media/` are
subfolders, never separate mounts:

```
/data                        <- one NFS export (volume4), identical mount in every *arr CT
├── torrents/{movies,tv}      <- qBittorrent writes here
└── media/{movies,tv}         <- *arr import here; Plex reads here
```

This is **BL-016's shared-export (path-bind) branch**, not the storage-backed
volume branch — a shared library can't be a single-CT `mpN`. So `/data` is a
path-bind of the volume4 export, gated by a pre-start **hookscript**
([`snippets/ensure-data-mount.sh`](snippets/ensure-data-mount.sh)) that refuses to
start a member unless the NAS is actually mounted — closing the CT 5007
rootfs-fill race.

## Members

| CTID | App | `/data` | dependsOn | Notes |
|---|---|:--:|---|---|
| 5100 | prowlarr | — | flaresolverr | indexer hub; migrate existing indexer DB at cutover |
| 5101 | sonarr | ✅ | prowlarr, qbittorrent | TV |
| 5102 | radarr | ✅ | prowlarr, qbittorrent | movies |
| 5103 | bazarr | ✅ | sonarr, radarr | subtitles (sidecar files) |
| 5104 | qbittorrent | ✅ | — | downloads → `/data/torrents` (BL-016 recut of CT 5007) |
| 5105 | seerr | — | sonarr, radarr | request/discovery UI (Plex-native) |
| 5107 | flaresolverr | — | — | Cloudflare-challenge solver for Prowlarr |

`5106` is reserved (a 2nd torrent client — Deluge — if ever needed). Cleanup
automation (Maintainerr, Docker-only) is **out of scope** → deferred to #106.

## Ownership (no PUID/PGID)

The community-scripts *arr installers are native systemd services running **as
root** — there is no PUID/PGID/UMASK model. Ownership is therefore enforced **at
the NFS export** (squash → fixed media uid/gid, setgid dirs + default group ACLs),
not per app. See the runbook.

## Deploy / redeploy / teardown

Deploy via community-scripts over SSH — `Deploy-Shape.ps1` or the converge engine:

```bash
homelab-infra converge stacks/Media               # read-only plan (diff vs live)
homelab-infra converge stacks/Media --apply       # create + reconcile cores/memory/tags
homelab-infra converge stacks/Media --destroy --yes   # teardown (reverse dep order)
```

> ⚠️ **Engine gap (converge does not apply mounts/hookscript yet).** `--apply`
> currently creates the CT and reconciles cores/memory/tags (#101), but does **not**
> provision `spec.mounts` or `spec.hookscript`. Until a converge mount-apply
> increment lands, wire `/data` + the hookscript by hand using the steps in the
> [volume4 runbook](../../docs/runbooks/volume4-data-export.md#3-attach-data--the-hookscript-per-member).
> The shapes already declare the desired mounts, so they're the source of truth
> the apply step will eventually consume.

## Cutover (summary — see the plan)

- **Indexers:** restore Prowlarr's `/var/lib/prowlarr` config DB into 5100.
- **Quality profiles:** start fresh via Recyclarr (TRaSH profiles as code).
- **Library:** curate down to the current watchlist; re-import under `/data/media`.
- Retire the old 5000s CTs once the new stack is verified end-to-end.
