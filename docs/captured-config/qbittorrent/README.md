# qBittorrent config capture — CT 5007 (`hpe-01`)

Version-controlled capture of the **live** qBittorrent settings, so the
seeding/share-limit policy survives a CT rebuild or revert and feeds the
[#105 declarative *arr stack](../../plans/105-arr-declarative-stack.md) rebuild
(future `stacks/Media`, CTID 5104). Today qBittorrent is a hand-built CT and is
**not otherwise in IaC**.

- **Captured:** 2026-06-25 from `/root/.config/qBittorrent/qBittorrent.conf`.
- **Container:** CT 5007 `qbittorrent`, `192.168.179.157`, Debian 12,
  community-scripts install, `qbittorrent-nox` as **root** (no PUID/PGID model).
- **Sanitised:** `WebUI\Password_PBKDF2` is redacted — never commit the hash.

## Why this exists — the hit-and-run near-miss

A private tracker flagged a **hit-and-run** (H&R) on this instance: a torrent
was downloaded but not seeded back enough (minimum ratio and/or seed time) before
seeding stopped. Repeated H&R on a private tracker can lose the account. The
settings have since been corrected; this capture pins the known-good policy.

## The seeding settings that matter (`[BitTorrent]`)

| Key | Live value | Effect |
|---|---|---|
| `Session\ShareLimitAction` | `Stop` | When a share limit is reached, **pause** the torrent. Safe. The H&R-causing values are `Remove` / `RemoveWithContent` — never use those. |
| `Session\GlobalMaxRatio` | *absent → `-1`* | No global ratio cap → seed indefinitely. |
| `Session\GlobalMaxSeedingMinutes` | *absent → `-1`* | No seed-time cap. |
| `Session\GlobalMaxInactiveSeedingMinutes` | *absent → `-1`* | No inactivity cap. |

**The fragility:** the current safety rests on those three keys being *absent*
and defaulting to unlimited. One accidental WebUI toggle — set a ratio and flip
the action to *Remove* — silently re-arms the H&R failure mode. So the policy
should be made **explicit**, not implicit.

## Hardened policy — APPLIED on the live box 2026-06-25

These are now set **explicitly** so the no-H&R policy is documented and
regression-proof (applied via stop → edit → start; verified to survive
qBittorrent's own graceful-shutdown save cycle). On rebuild, set the same:

```ini
[BitTorrent]
Session\ShareLimitAction=Stop
Session\GlobalMaxRatio=-1
Session\GlobalMaxSeedingMinutes=-1
Session\GlobalMaxInactiveSeedingMinutes=-1
```

> A timestamped backup of the pre-hardening file is on the CT at
> `/root/.config/qBittorrent/qBittorrent.conf.bak-20260625` (rollback safety net).

If you ever *do* want a ratio cap, keep it **above every private tracker's
requirement** (e.g. `2.0`) and **always** keep the action on `Stop`. Better still,
set per-tracker share limits rather than a global cap. For strict trackers,
prefer Sonarr/Radarr seeding-aware cleanup (or Maintainerr, deferred to #106) so
nothing is removed before its seed obligation is met.

## ⚠️ Operational persistence gotcha

qBittorrent holds settings **in memory** and only flushes `qBittorrent.conf` on a
**graceful shutdown** (plus a periodic autosave). Consequences:

1. A WebUI change can be lost if the CT is **force-stopped/crashes** before the flush.
2. **Editing `qBittorrent.conf` directly while the service runs gets clobbered**
   on exit.

To change settings durably on the live box, do it via the **WebUI/Web API**, or
**stop the service → edit the file → start** — never edit it live. To re-capture
into this repo after a change:

```bash
ssh root@192.168.179.3 "pct exec 5007 -- cat /root/.config/qBittorrent/qBittorrent.conf"
```
