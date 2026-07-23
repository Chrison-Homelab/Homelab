# ADR-0006 — Media (*arr) stack: declarative side-by-side rebuild + dedicated Cloudflare tunnel

- **Status:** Proposed
- **Date:** 2026-06-14
- **Deciders:** Chris
- **Relates to:** [ADR-0004 storage](ADR-0004-storage-architecture.md),
  [ADR-0005 tunnel topology](ADR-0005-cloudflare-tunnel-topology.md),
  [#105 declarative *arr stack](../plans/105-arr-declarative-stack.md),
  [BL-013 community-scripts deploy](../plans/BL-013-community-scripts-deploy.md),
  [BL-016 NFS hardening](../plans/BL-016-nfs-hardening.md)
- **Defers to:** [#106 Komodo + Maintainerr](https://github.com/Chrison-Homelab/Homelab/issues/106)

## Context

The *arr media fleet runs today as **hand-built CTs in the ~5000s block** (prowlarr,
sonarr, radarr, bazarr, qbittorrent, seerr, flaresolverr, tracearr; plus a Plex CT +
Plex-VM) — **none captured in IaC**, mid network-migration (legacy `192.168.179.x` ↔
`1010`), and the source of the qBittorrent rootfs-fill NFS race. Publicly they ride the
**monolithic `tao-simon.family`** tunnel (CT 2001), the last big tenant blocking that
monolith's retirement.

We want a **clean rebuild**, not a patch-up — and a **safe** one: build a parallel stack,
verify it end-to-end, then cut over, leaving the live fleet untouched until the switch.

This ADR formalises the *arr rebuild scoped in plan #105 **and adds the exposure layer**
(the part #105 left open): how the rebuilt stack is published, per the per-stack tunnel
model of ADR-0005.

## Decision

**Rebuild the *arr core as declarative `homelab/v1` shapes in a new `stacks/Media`
(5100 block), built side-by-side with the live 5000s fleet, fronted by a dedicated
`Homelab.Stacks.Media` tunnel on the clean `chrison.dev` slate.**

1. **Parallel build, fresh CTs.** New `stacks/Media` submodule-style dir (in-repo, like
   Core), CTID **5100-5199**, deployed via community-scripts over SSH. The live 5000s CTs
   keep running until the new stack is verified, then retire (#105).

2. **One shared `/data` NFS export (the load-bearing constraint).** Every file-touching CT
   (sonarr/radarr/bazarr/qbittorrent) mounts the **same single** export at the **same path
   `/data`**, with `torrents/` and `media/` as subfolders — so *arr hardlinks + instant-moves
   instead of copy+delete. This is **BL-016's shared-export branch** (path-bind + pre-start
   `mountpoint -q` hookscript guard), on **volume4** (8 TB). Ownership is enforced **at the
   NFS export** (`all_squash` + anonuid/anongid + setgid/ACLs), because the community-scripts
   *arr installers run as root with no PUID/PGID model.

3. **Dedicated tunnel + connector (ADR-0005).** A new `Homelab.Stacks.Media` tunnel with a
   cloudflared connector CT **inside VLAN 1010**; flat **`<svc>.chrison.dev`** hostnames
   (one DNS CNAME per service → the Media tunnel). Flat single-level naming because Universal
   SSL covers `*.chrison.dev` but **not** a nested `*.arr.chrison.dev` (that needs ACM, ~$10/mo)
   — promotable later.

4. **Auth — CF Access OTP on admin UIs (Teleport interim).** Each *arr admin UI sits behind a
   CF Access self-hosted app (One-Time PIN; allow `csimon@chrison.dev`), as for PDM/Proxmox.
   `flaresolverr` is **not exposed** (internal; Prowlarr calls it). Native-client apps that
   can't SSO (Plex, and audiobookshelf if later added) get **direct** hostnames. When Teleport
   lands (#117/BL-001) the admin UIs can move behind it; OTP is the interim door.

5. **Cutover (#105).** Carry over **Prowlarr's indexer DB**; start quality profiles **fresh**
   via **Recyclarr** (TRaSH-as-code); **curate the library down** to the current watchlist
   under `/data/media` (no bulk 2.8 TB copy); verify; retire the 5000s CTs.

### Target members + exposure

| CTID | App | `<svc>.chrison.dev` | Auth | `/data` |
|---|---|---|---|---|
| 5100 | prowlarr | `prowlarr` | CF Access OTP | — (indexers) |
| 5101 | sonarr | `sonarr` | CF Access OTP | ✅ |
| 5102 | radarr | `radarr` | CF Access OTP | ✅ |
| 5103 | bazarr | `bazarr` | CF Access OTP | ✅ |
| 5104 | qbittorrent | `qbittorrent` | CF Access OTP | ✅ |
| 5105 | seerr | `seerr` | CF Access OTP | — |
| 5106 | *(reserved — 2nd torrent client, e.g. Deluge)* | — | — | ✅ |
| 5107 | flaresolverr | *(not exposed)* | — | — |
| 5108 | cloudflared (connector) | *(serves the tunnel)* | — | — |

`seerr.chrison.dev` is the **admin** view; the family keeps the untouched
`seerr.tao-simon.family`. Plex stays as-is; if/when published it gets a **direct**
`plex.chrison.dev` on this tunnel.

## Alternatives considered

- **In-place fix of the 5000s CTs** — rejected: no IaC capture, risks the live fleet, and
  doesn't fix the rootfs-fill race.
- **Per-app separate NFS mounts** (library vs downloads) — rejected: breaks hardlinking
  (TRaSH); forces slow copy+delete and 2× disk. Single `/data` export is mandatory.
- **Nested `*.arr.chrison.dev`** — deferred: needs ACM. Flat `<svc>.chrison.dev` is free
  under Universal SSL and promotable later.
- **Everything behind Teleport now** — Teleport is parked (#117); OTP is the interim gate.

## Consequences

- **+** *arr stack becomes reproducible/redeployable IaC; clean tunnel boundary (retire-able
  with the stack); admin UIs gated; the old monolith loses its biggest *arr tenant.
- **−** Depends on **volume4** being exported + wired (manual DSM; blocks the data move).
- **−** Shared path-bind `/data` mount model must be expressed in the shape schema (open item).
- **−** Library curation is a manual judgement pass.
- **−** Two access patterns (OTP admin vs direct Plex), documented per service.

## Out of scope

- **Plex** (CT + Plex-VM) — left as-is.
- **Maintainerr + Komodo** docker revival → #106.
- **audiobookshelf / shelfmark / romm / tautulli / tracearr** — media-adjacent; not part of
  this *arr-core rebuild (can be folded in later as their own members).
- `tao-simon.family` domain — left entirely untouched (the double-up is accepted).
- Storage-layer abstraction (NAS-VM fronting the Synology) → future.
