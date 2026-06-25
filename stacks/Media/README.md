# Homelab.Stacks.Media

Declarative **side-by-side rebuild** of the *arr media fleet as `homelab/v1` shapes,
deployed via community-scripts LXCs and fronted by a dedicated Cloudflare tunnel.

> Design: [ADR-0006](../../docs/adr/ADR-0006-media-stack.md) · scope/cutover detail:
> [plan #105](../../docs/plans/105-arr-declarative-stack.md). Built **alongside** the
> live legacy 5000-block fleet, verified, then cut over — the old fleet keeps running
> until the switch.

## Members

CTID block **5100–5199** (declared in [`stack.yaml`](stack.yaml); members inherit its `defaults`).

| CTID | Member | `<svc>.chrison.dev` | Auth | `/data` | Status |
|------|--------|---------------------|------|---------|--------|
| 5100 | [prowlarr](prowlarr.lxc.yaml) | `prowlarr` | CF Access OTP† | — | ✅ |
| 5101 | [sonarr](sonarr.lxc.yaml) | `sonarr` | CF Access OTP† | ✅ | ✅ |
| 5102 | [radarr](radarr.lxc.yaml) | `radarr` | CF Access OTP† | ✅ | ✅ |
| 5103 | [bazarr](bazarr.lxc.yaml) | `bazarr` | CF Access OTP† | ✅ | ✅ |
| 5104 | [qbittorrent](qbittorrent.lxc.yaml) | `qbittorrent` | CF Access OTP† | ✅ | ✅ |
| 5105 | [seerr](seerr.lxc.yaml) | `seerr` | CF Access OTP† | — | ✅ |
| 5106 | [cross-seed](cross-seed.lxc.yaml) | *(not exposed)* | — | ✅ | ✅ |
| 5107 | [flaresolverr](flaresolverr.lxc.yaml) | *(not exposed)* | — | — | ✅ |
| 5108 | [cloudflared](cloudflared.lxc.yaml) | *(serves the tunnel)* | — | — | ✅ |
| 5109 | [tracearr](tracearr.lxc.yaml) | *(internal-only)* | — | — | ✅ |
| 5110 | [romm](romm.lxc.yaml) | *(internal-only)* | — | `roms`‡ | ✅ |
| 5111 | [shelfmark](shelfmark.lxc.yaml) | *(internal-only)* | — | `books`‡ | ✅ |
| 5112 | [audiobookshelf](audiobookshelf.lxc.yaml) | `audiobookshelf` | direct (own auth) | `audiobooks`‡ | ✅ |
| 5113 | [youtarr](youtarr.lxc.yaml) | *(internal-only)* | — | `youtube`‡ | ✅ |

† Auth is **deferred to stage 2** (CF Access OTP vs Pangolin/ADR-0007 — decided later); the shapes/tunnel ship first.
Post-#192 the tunnel routes **only** `seerr` + `audiobookshelf`; the *arr admin UIs are internal-only.
‡ Binds a **non-`/data`** volume4 subpath (e.g. `roms`/`books`/`audiobooks`/`youtube`) at its own library path, not the shared `/data` export.

> **5113 (youtarr) is the stack's first Docker-based member** — no `ct/youtarr.sh`
> exists, so it's a thin Docker host (`app: docker` → `ct/docker.sh`) with a
> self-contained compose layered on. See [`youtarr/README.md`](youtarr/README.md).
> Every other member is a native community-scripts install.

`seerr.chrison.dev` is the **admin** view; the family keeps the untouched
`seerr.tao-simon.family`. **Plex** stays as-is (not rebuilt); if published it gets a
**direct** `plex.chrison.dev` (native clients can't SSO).

## The shared `/data` constraint

Every file-touching member (sonarr/radarr/bazarr/qbittorrent) mounts the **same single**
NFS export (volume4) at the **same path `/data`**, with `torrents/` + `media/` as
**subfolders** — so *arr **hardlinks + instant-moves** instead of copy+delete.

**NAS-safety wiring** (layered, from the 2026-06-20 bind-mount spike — see memory
`nas-drop-failure-modes`): rootfs-fill is a *boot-state* hazard, prevented structurally by an
**immutable underlying mountpoint** + the pre-start [`ensure-data-mount.sh`](snippets/ensure-data-mount.sh)
hookscript (no NAS → CT won't start, can't write to rootfs). *Runtime* NAS loss can't leak but
*hangs* the app, so each file-touching CT also runs the per-CT
[`nas-watchdog`](snippets/nas-watchdog.sh) (drop → stop the app; return → restart). volume4's
export is **provisioned via SynoSharp** (reproducible IaC); register it as the Proxmox storage
`ds1813-nfs-volume-4` (task #1). Wiring all of this at converge/deploy time = task #13.

```
/data                        <- one NFS export, identical mount in every *arr CT
├── torrents/{movies,tv}      <- qBittorrent writes here
└── media/{movies,tv}         <- *arr import here; Plex reads here
```

## Deploying

These are LXC shapes for the `homelab/v1` contract — render/deploy from the parent repo:

```powershell
# from the Homelab checkout — dry-run by default:
./Infrastructure/deploy/Deploy-Shape.ps1 -ShapePath ./stacks/Media/cloudflared.lxc.yaml
# add -Apply to deploy over SSH
```

Order: **volume4 export (task #1)** → connector + tunnel → **prowlarr** → sonarr/radarr/
bazarr/qbittorrent → seerr → flaresolverr → DNS + CF Access → cutover. See the task list.

Notes:
- Schema: [`Infrastructure/schema/shape.schema.json`](../../Infrastructure/schema/shape.schema.json).
- ADD-ONLY on Cloudflare: the `Homelab.Stacks.Media` tunnel is new; never touches CT 2001.
- Cutover (#105): carry over Prowlarr's indexer DB; **fresh** profiles via Recyclarr; curate
  the library down to the watchlist (no bulk copy); verify; retire the old 5000s CTs.
