"""Tidy up the reorg. RUN FROM Terminal.app with the osxphotos env:
    preview:  ~/.local/bin/uv run --with osxphotos python ~/photo-dedup/reorg_tidy.py
    test:     ... reorg_tidy.py test     (deletes ONE mislabeled album to prove delete works)
    apply:    ... reorg_tidy.py apply

Plan: (1) recreate trip albums properly NESTED under a 'Trips/' folder,
      (2) delete the ~40 mislabeled flat 'Trips/...' albums,
      (3) dissolve the 225 date (YYYY / MM) albums.
Photos are never deleted -- only album grouping changes."""
import os, re, sys, json, time
from osxphotos import PhotosDB, PhotosAlbum

HERE = os.path.dirname(os.path.abspath(__file__))
MODE = sys.argv[1] if len(sys.argv) > 1 else "preview"
plan = json.load(open(os.path.join(HERE, "reorg_plan.json")))
log = open(os.path.join(HERE, "reorg_tidy.log"), "a")
def say(*a):
    m = " ".join(str(x) for x in a); print(m, flush=True); log.write(m+"\n"); log.flush()

YEAR=re.compile(r'^(19|20)\d{2}$'); MON=re.compile(r'^(0?[1-9]|1[0-2])$'); YM=re.compile(r'^(19|20)\d{2}[ _/\-.]?(0?[1-9]|1[0-2])$')
def datey(t):
    t=(t or '').strip(); return bool(YEAR.match(t) or MON.match(t) or YM.match(t))

db = PhotosDB()
flat_trips = [a for a in db.album_info if (a.title or '').startswith("Trips/")]
date_albums = [a for a in db.album_info if datey(a.title)]
say(f"\n=== reorg_tidy {MODE} {time.strftime('%H:%M:%S')} ===")
say(f"mislabeled flat 'Trips/...' albums to delete: {len(flat_trips)}")
say(f"date (YYYY/MM) albums to dissolve:            {len(date_albums)}")
say(f"trip albums to (re)create nested:             {len(plan['trip_albums'])}")

if MODE == "preview":
    say("\n-- would DELETE these flat albums (sample) --")
    for a in flat_trips[:8]: say(f"   {a.title}  ({len(a.photos)})")
    say("-- would DISSOLVE date albums (sample) --")
    for a in sorted(date_albums,key=lambda x:-len(x.photos))[:8]: say(f"   {'/'.join(list(a.folder_names)+[a.title])}  ({len(a.photos)})")
    say("-- would CREATE nested (sample) --")
    for t in plan['trip_albums'][:8]: say(f"   Trips/{t['name']}  ({t['count']})")
    say("\nPREVIEW only. Run 'test' next, then 'apply'.")
    sys.exit(0)

import photoscript
lib = photoscript.PhotosLibrary()

def delete_album_by_uuid(uuid):
    """Try direct Album(uuid); return True on success."""
    lib.delete_album(photoscript.Album(uuid)); return True

if MODE == "test":
    # delete the single smallest mislabeled flat album as a proof
    target = min(flat_trips, key=lambda a: len(a.photos)) if flat_trips else None
    if not target: say("no flat 'Trips/...' albums found to test on."); sys.exit(0)
    say(f"TEST: deleting one mislabeled album '{target.title}' ({len(target.photos)} photos; photos stay in library)")
    try:
        delete_album_by_uuid(target.uuid)
        say("  -> delete call succeeded. Verify in Photos that the album is gone, then run 'apply'.")
    except Exception as e:
        say(f"  -> FAILED: {e}")
        say("  Do NOT run apply; tell me this error.")
    sys.exit(0)

if MODE == "apply":
    # 1) recreate nested trip albums
    made = 0
    for t in plan['trip_albums']:
        try:
            alb = PhotosAlbum(f"Trips/{t['name']}", split_folder="/")
            photos = db.photos(uuid=t['uuids'])
            if photos: alb.update(photos)
            made += 1
        except Exception as e:
            say(f"  ERROR creating Trips/{t['name']}: {e}")
    say(f"created {made}/{len(plan['trip_albums'])} nested trip albums")
    # SAFETY GUARD: never delete the flat originals unless every nested album was created
    if made < len(plan['trip_albums']):
        say(f"ABORTING before any deletion: only {made}/{len(plan['trip_albums'])} nested albums created.")
        say("Flat albums + date albums left untouched. Fix creation, then re-run apply.")
        sys.exit(1)
    # 2) delete mislabeled flat albums
    df = 0
    for a in flat_trips:
        try: delete_album_by_uuid(a.uuid); df += 1
        except Exception as e: say(f"  ERROR deleting {a.title}: {e}")
    say(f"deleted {df}/{len(flat_trips)} mislabeled flat albums")
    # 3) dissolve date albums
    dd = 0
    for a in date_albums:
        try: delete_album_by_uuid(a.uuid); dd += 1
        except Exception as e: say(f"  ERROR dissolving {a.title}: {e}")
    say(f"dissolved {dd}/{len(date_albums)} date albums")
    say("DONE.")
