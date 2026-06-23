# Photo cleanup — Mac-side prototypes

Validated, throwaway-quality **reference scripts** from the iCloud-photo-cleanup effort (run locally on a MacBook M4 against the photo backup on the NAS). They are the proven logic the containerized **vision engine (#184)** and **monthly pipeline (#187)** should be built from — *not* production code.

> ⚠️ Prototypes: hard-coded paths (`/Users/csimon/...`, `/Volumes/iCloud Backup Chris/PhotosBackup`, a session scratchpad), Mac-only deps in places (`ocrmac` = Apple Vision; `osxphotos`/`photoscript` = Photos library). For the LXC engine, parameterize paths and keep only the OS-agnostic compute (CLIP/OCR/pHash). No personal data files are committed — only code.

## Pipeline shape (what worked)

```
osxphotos (Mac) ──export images+metadata──▶ NAS ──▶ compute (CLIP/OCR/pHash) ──▶ uuid→bucket JSON ──▶ osxphotos --add-to-album (Mac, Terminal.app) ──▶ Review-* album ──▶ manual delete
```

## Scripts

**Dedup (step 2)**
- `dedup_scan.py` — pHash every backup image → JSONL cache (resumable).
- `dedup_plan.py` — asset-level union-find (name + perceptual edges) + keep-rules → removal plan + review CSV. **Reference dedup logic.**
- `phash_preview.py` — early cross-name dup preview.

**Folder reorg (step 3)**
- `reorg_build.py` — cluster unorganized photos into trips (gap + home/residence filters) → plan JSON.
- `reorg_dryrun.py` — clustering dry-run/preview.
- `reorg_tidy.py` / `reorg_apply.py` — apply via `osxphotos.PhotosAlbum(split_folder="/")` for nesting + bundled `photoscript` for deletes. **Note:** standalone `photoscript` is incompatible with macOS 26 / Photos 11 — use the version `osxphotos` pins; album mutation only works from a GUI-attributable terminal (Terminal.app, not headless/wave).

**Junk classification (step 4)** — the bits most relevant to #184
- `junk_dryrun.py` — shows why Apple AI labels alone are too noisy for memes/furniture/grocery.
- `clip_classify.py` — CLIP zero-shot (open_clip ViT-B-32 / laion2b).
- `clip_embed.py` — **cache image embeddings once** so prompt tuning is free afterward.
- `clip_tune.py` — instant prompt/gate experiments vs the cache. **v1 config: gate `CONF_MIN=0.6, MARGIN=0.25`, keeper categories incl. `vehicle`/`object_other`.** Reliable bucket = `screenshot_doc`; furniture usable; grocery/meme unreliable via CLIP.
- `ocr_memes.py` — Apple Vision OCR (text amount + bbox-area coverage). Finding: surfaces extra screenshots/text-junk CLIP missed; pure "memes" are a fuzzy subset (short caption + WhatsApp origin).

See #184 (engine) and #187 (monthly pipeline) for the productionization plan and tuning notes.
