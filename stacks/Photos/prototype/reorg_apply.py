"""Apply the library reorg via PhotoScript (drives Photos.app).
RUN FROM Terminal.app (needs the 'control Photos' automation grant).

  preview (default):  uv run --with photoscript python reorg_apply.py
  for real:           uv run --with photoscript python reorg_apply.py apply

Reads reorg_plan.json next to this script. Creates 'Trips/' albums + adds photos,
then dissolves the date-cruft albums. Idempotent-ish: skips albums that already exist.
Photos are never deleted — dissolving only removes album grouping."""
import json, os, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
plan = json.load(open(os.path.join(HERE, "reorg_plan.json")))
APPLY = len(sys.argv) > 1 and sys.argv[1] == "apply"
log = open(os.path.join(HERE, "reorg_apply.log"), "a")
def say(*a):
    m = " ".join(str(x) for x in a); print(m, flush=True); log.write(m + "\n"); log.flush()

say(f"\n=== reorg {'APPLY' if APPLY else 'PREVIEW'} {time.strftime('%Y-%m-%d %H:%M:%S')} ===")
say(f"trip albums: {len(plan['trip_albums'])} | dissolve: {len(plan['dissolve_albums'])} | backfill: {len(plan['backfill'])}")
if not APPLY:
    say("PREVIEW only — re-run with 'apply' to make changes.")

from photoscript import PhotosLibrary, Album
lib = PhotosLibrary()

# ---- find the Trips folder (create manually in Photos first); fall back to top-level ----
def find_trips_folder():
    # try a few photoscript signatures to locate an existing 'Trips' folder
    for call in (lambda: lib.folder("Trips", top_level=True),
                 lambda: lib.folder("Trips"),
                 lambda: next((f for f in lib.folders() if f.name == "Trips"), None)):
        try:
            f = call()
            if f: return f
        except Exception: pass
    return None

existing_album_names = set()
try: existing_album_names = set(lib.album_names())
except Exception: pass

trips = None
if APPLY:
    trips = find_trips_folder()
    if trips is None:
        try:
            trips = lib.create_folder("Trips")
        except Exception as e:
            say(f"  (could not create 'Trips' folder: {e})")
            say("  -> falling back to TOP-LEVEL albums. To group them, create an empty")
            say("     'Trips' folder in Photos (sidebar right-click > New Folder) and re-run.")
            trips = None

# ---- create trip albums + add photos ----
made = added = skipped = 0
for t in plan["trip_albums"]:
    name = t["name"]
    if name in existing_album_names:
        say(f"  skip (exists): {name}"); skipped += 1; continue
    say(f"  album: Trips/{name}  (+{t['count']} photos)")
    if not APPLY: continue
    try:
        alb = lib.create_album(name, folder=trips) if trips else lib.create_album(name)
        # add in chunks
        uuids = t["uuids"]
        for i in range(0, len(uuids), 100):
            photos = list(lib.photos(uuid=uuids[i:i+100]))
            if photos: alb.add(photos)
        made += 1; added += t["count"]
    except Exception as e:
        say(f"    ERROR creating/adding {name}: {e}")

# ---- dissolve date-cruft albums ----
dissolved = 0
for a in plan["dissolve_albums"]:
    say(f"  dissolve: {a['path']}  ({a['count']} photos stay in library)")
    if not APPLY: continue
    try:
        lib.delete_album(Album(a["uuid"]))
        dissolved += 1
    except Exception as e:
        say(f"    ERROR dissolving {a['path']}: {e}")

# ---- date backfill (tiny) ----
if APPLY and plan["backfill"]:
    import datetime as dt
    for b in plan["backfill"]:
        try:
            for ph in lib.photos(uuid=[b["uuid"]]):
                ph.date = dt.datetime.fromisoformat(b["date"])
        except Exception as e:
            say(f"    ERROR backfilling {b['uuid']}: {e}")

if APPLY:
    say(f"\nDONE: created {made} albums (+{added} photos), skipped {skipped}, dissolved {dissolved} date albums.")
else:
    say(f"\nPREVIEW done. Would create {len(plan['trip_albums'])-skipped}, dissolve {len(plan['dissolve_albums'])}.")
