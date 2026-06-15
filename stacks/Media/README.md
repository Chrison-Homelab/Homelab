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
| 5100 | prowlarr | `prowlarr` | CF Access OTP | — | shape TBD (task #4) |
| 5101 | sonarr | `sonarr` | CF Access OTP | ✅ | shape TBD |
| 5102 | radarr | `radarr` | CF Access OTP | ✅ | shape TBD |
| 5103 | bazarr | `bazarr` | CF Access OTP | ✅ | shape TBD |
| 5104 | qbittorrent | `qbittorrent` | CF Access OTP | ✅ | shape TBD |
| 5105 | seerr | `seerr` | CF Access OTP | — | shape TBD |
| 5106 | *(reserved — 2nd torrent client, e.g. Deluge)* | — | — | ✅ | reserved |
| 5107 | flaresolverr | *(not exposed)* | — | — | shape TBD |
| 5108 | [cloudflared](cloudflared.lxc.yaml) | *(serves the tunnel)* | — | — | ✅ shape ready |

`seerr.chrison.dev` is the **admin** view; the family keeps the untouched
`seerr.tao-simon.family`. **Plex** stays as-is (not rebuilt); if published it gets a
**direct** `plex.chrison.dev` (native clients can't SSO).

## The shared `/data` constraint

Every file-touching member (sonarr/radarr/bazarr/qbittorrent) mounts the **same single**
NFS export (volume4) at the **same path `/data`**, with `torrents/` + `media/` as
**subfolders** — so *arr **hardlinks + instant-moves** instead of copy+delete. Gated by a
pre-start `mountpoint -q` hookscript (BL-016 shared-export branch). **volume4 must be
provisioned first** (manual DSM — task #1).

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
