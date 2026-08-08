# Rack model files

Downloaded 3D model files for the rack mounting system. The plan, dimensions and the
full model manifest live in [`../../Rack.md`](../../Rack.md) — **read that first.**

## What may be committed here, and what may not

The OpenRack ecosystem uses **two different licences**, and they are not
interchangeable. Check every file's source page before adding it.

| Source | Licence | Committed? |
|---|---|---|
| **Sparco's base + blank insert** (`openrack-1u-19in-base*`, `openrack-1u-blank-insert*`) | Standard Digital File License — forbids hosting/distributing on other platforms | ❌ **gitignored** |
| **Community inserts** (EliteDesk G2, UCG Ultra, NUC, …) | Usually CC BY-NC-SA — permits redistribution with attribution + ShareAlike | ✅ allowed, **with credit recorded in the manifest** |

The gitignore rules in the repo root enforce the first row. They are deliberately
name-based: **if you add a Sparco file under a different name it will be committed.**

## Downloading

**MakerWorld requires a logged-in account to download** — the model pages expose no
anonymous download. Fetch them by hand from the URLs in the manifest.

Suggested naming, so the gitignore rules keep working:

```
openrack-1u-19in-base.stl              # ignored (Sparco)
openrack-1u-blank-insert.stl           # ignored (Sparco)
insert-elitedesk-800-g2.stl            # committed — tartineskiller, CC BY-NC-SA
insert-ucg-ultra.stl                   # committed — TimBim, verify licence first
insert-nuc-d34010wyk.stl               # committed — remix target, see Rack.md
```

## Attribution

Every committed file must have its designer and licence recorded in the manifest table
in [`../../Rack.md`](../../Rack.md). CC BY-NC-SA requires attribution — an uncredited
file in this directory is a licence breach, not just untidy bookkeeping.
