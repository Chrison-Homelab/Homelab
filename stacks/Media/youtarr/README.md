# Youtarr — Docker layer (CT 5113)

[Youtarr](https://github.com/DialmasterOrg/Youtarr) auto-downloads subscribed
YouTube channels/playlists into a Plex library. It's the **first Docker-based
member** of the Media stack: there's no `community-scripts/ct/youtarr.sh` and no
native install, so the CT is a thin Docker host (`app: docker` → `ct/docker.sh`)
with this self-contained compose layered on. Two containers — the app
(`dialmaster/youtarr`) and a bundled MariaDB 10.3.

> Shape: [`../youtarr.lxc.yaml`](../youtarr.lxc.yaml) · internal-only, VLAN 1010.

## Why no repo clone

Upstream recommends `git clone` + their `start.sh`, but that wrapper only
generates `.env` and orchestrates *their* compose. The published images are
self-contained (`image:` pulls only; every volume is a data dir), so we ship a
minimal [`compose.yml`](compose.yml) + [`.env.example`](.env.example) and skip
the source tree entirely. Lower overhead, fully in-repo.

## Layout on the CT

```
/opt/youtarr/                <- compose + app state (CT local disk)
├── compose.yml              <- this file (copied over)
├── .env                     <- from .env.example (gitignored; DB pw in Bitwarden)
├── config/  jobs/  server/images/   <- app state
└── database/                <- bundled MariaDB store
/mnt/youtube                 <- volume4 `youtube` subpath (NFS) → container /usr/src/app/data
```

## Deploy (one-time, after the CT exists)

`ct/docker.sh` already installed Docker + compose on create. Then:

```bash
# on CT 5113
install -d /opt/youtarr && cd /opt/youtarr
# copy compose.yml + .env.example from this repo dir to /opt/youtarr, then:
cp .env.example .env
# set DB_ROOT_PASSWORD (bw generate → Bitwarden); YOUTUBE_OUTPUT_DIR=/mnt/youtube is preset
install -d config jobs server/images database          # YOUTUBE_OUTPUT_DIR is the NFS mount
docker compose up -d
```

Web UI: `http://<ct-ip>:3087` (internal-only).

## Post-deploy (UI steps)

1. Create the Youtarr admin login.
2. Point Youtarr at Plex (Settings → Plex URL/token), or preset `PLEX_URL` in `.env`.
3. In **Plex**, add a library pointing at the `youtube` subpath of volume4
   (Plex's host/CT must also see that path) so downloads show up.
4. Subscribe channels/playlists.

## Scope

This is a **media-only** Docker host — don't throw unrelated apps onto it. A
heavier orchestrator (e.g. **Komodo**, [`../../Komodo`](../../Komodo)) is out of
scope for now; revisit it only if a **second media Docker app** lands.
