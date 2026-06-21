# Plan: #159 — `arr-wire` config migration (self-contained, side-by-side)

**Issue:** [#159](https://github.com/Chrison-dev/Homelab/issues/159) ·
**Builds on:** [#105 declarative stack](105-arr-declarative-stack.md), [ADR-0006](../adr/ADR-0006-media-stack.md) ·
**Status:** Building — 2026-06-21 · branch `feat/159-arr-wire`.

## Goal

The Media stack (CTs 5100–5108) is deployed + live but the *arr apps are **empty**.
Wire them into a **self-contained** working fleet — new→new only (new sonarr/radarr →
new prowlarr 5100 + new qbittorrent 5104 → `/data` on volume4), **not** cross-wired to
the old 5000-block fleet. **No cutover** — both run side-by-side. Done as a re-runnable,
converge-style IaC provisioner, not a throwaway script.

## Method

Converge-style **per-app provisioners** (`IAppProvisioner`, keyed by `spec.app`,
registered in `ProvisionerRegistry.Default()`). Each runs at `converge --apply` in
dependsOn order and is **idempotent**: read the app's REST API (GET) and POST only the
missing config. Inputs:

- **Own API key** — read live from the CT: `pct exec <ctid> -- cat /var/lib/<app>/config.xml`
  → `<ApiKey>` (sonarr/radarr/prowlarr). qBittorrent uses WebUI creds; Seerr a settings file.
- **Own URL** — `http://<ct-ip>:<port>`; IP read live (`pct exec <ctid> -- hostname -I`),
  port fixed per app (prowlarr 9696, sonarr 8989, radarr 7878, qbit 8080).
- **Sibling refs** — `ConvergeContext.ByName` → sibling shape → ctid → read its key/IP.

### Ordering insight — *arr self-registers into Prowlarr

Prowlarr is processed **first** (everyone `dependsOn` it), but registering Sonarr/Radarr
as Prowlarr **Applications** needs *their* URL+key, which don't exist at Prowlarr's turn.
So **each *arr self-registers into Prowlarr** from its own provisioner (runs after Prowlarr
is up; reads Prowlarr's key via `ByName`). Keeps each app's wiring self-contained.

## Scope — THIS pass: the core spine only

| Provisioner | Does | API |
|---|---|---|
| **Qbittorrent** (5104) | set WebUI creds (secrets.env `QBIT_USER/QBIT_PASSWORD`); categories `tv-sonarr`,`radarr`; save path `/data/torrents` (+ per-category `/data/torrents/{tv,movies}`) | qbit v2: `/api/v2/auth/login`, `/api/v2/torrents/categories`, `/api/v2/app/setPreferences` |
| **Prowlarr** (5100) | carry indexers from **OLD prowlarr 5002** (GET old `/api/v1/indexer` → POST new, skip-by-name); add **FlareSolverr 5107** proxy | prowlarr v1: `/api/v1/indexer`, `/api/v1/indexerproxy` |
| **Sonarr** (5101) | root folder `/data/media/tv`; add qbit download client (category `tv-sonarr`); **self-register into Prowlarr** | sonarr v3: `/api/v3/rootfolder`, `/api/v3/downloadclient`; prowlarr `/api/v1/applications` |
| **Radarr** (5102) | root folder `/data/media/movies`; add qbit download client (category `radarr`); **self-register into Prowlarr** | radarr v3 (as sonarr) |

**Deferred** (follow-ups, by decision 2026-06-21):
- **Bazarr** (→ arr) — after the spine is proven.
- **Seerr** — its `/setup` wizard resists headless automation (Plex token bootstrap); deferred entirely.
- **Recyclarr** (TRaSH profiles) — use the apps' **default** profiles first; add `recyclarr.yml` + sync later.
- **DHCP reservations** for the new CTs (#117) — provisioners read IPs live, so churn just needs a re-converge; reservations make it stable.

## Open wrinkle — qBittorrent credential bootstrap

community-scripts' qbittorrent-nox (4.6+) generates a **random temporary WebUI password**
on first run (logged to the service journal). The provisioner must bootstrap from that
(read via `Exec`) before it can `setPreferences` to the desired `QBIT_*` creds. Exact
behaviour confirmed on first apply against CT 5104.

## Order of work

1. `ArrApi`/`QbitApi`/`ProwlarrApi` thin REST clients (Providers.cs style) + key-read helper.
2. `QbittorrentProvisioner` → `ProwlarrProvisioner` → `Sonarr`/`Radarr` provisioners; register in `ProvisionerRegistry.Default()`; add each to `app-catalogue.yaml` (drift-guard test).
3. Faked-`Exec` unit tests for idempotency (no live cluster).
4. `converge stacks/Media` dry-run → **`deploy-media` apply on the runner** (needs #162 fix live).
5. Verify end-to-end: a test grab in Sonarr → qbit downloads to `/data/torrents` → import to `/data/media`. Side-by-side; old fleet untouched.

## Out of scope

Cutover / retiring the old 5000s fleet; library bulk migration; Plex re-pointing.
