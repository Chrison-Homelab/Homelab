"""Planning pass (read-only). Emits reorg_plan.json + reorg_names.txt.
Trip clusters (MIN=15, home/residence filtered) + the date-cruft albums to dissolve (by uuid)."""
import json, re, collections, datetime as dt
from osxphotos import PhotosDB

SCRATCH="/private/tmp/claude-501/-Users-csimon-Github-Chrison-dev-RemoteHarness/eca58170-581a-4088-99c2-a5ac86bbed43/scratchpad"
GAP_H, MIN = 36, 15

d=json.load(open(f"{SCRATCH}/all.json"))
nh=[p for p in d if not p.get('hidden')]
YEAR=re.compile(r'^(19|20)\d{2}$'); MON=re.compile(r'^(0?[1-9]|1[0-2])$'); YM=re.compile(r'^(19|20)\d{2}[ _/\-.]?(0?[1-9]|1[0-2])$')
def datey(leaf):
    leaf=leaf.strip(); return bool(YEAR.match(leaf) or MON.match(leaf) or YM.match(leaf))
def apaths(p):
    folders=p.get('folders') or {}; return ['/'.join(folders.get(a,[])+[a]) for a in (p.get('albums') or [])]
def organized(p): return any(not datey(x.split('/')[-1]) for x in apaths(p))
def parse(s):
    try: return dt.datetime.fromisoformat(s)
    except Exception: return None
def cdate(p): return parse(p.get('date') or p.get('date_original') or '')
def ishome(p): return (p.get('place') or {}).get('ishome')
def locality(p):
    ad=(p.get('place') or {}).get('address') or {}
    if not ad.get('country'): return None
    return ad.get('city') or ad.get('sub_locality') or ad.get('sub_administrative_area') or ad.get('state_province')

# ---- clusters ----
pool=[p for p in nh if not organized(p) and cdate(p)]
pool.sort(key=cdate)
groups=[]; cur=[]; last=None
for p in pool:
    t=cdate(p)
    if last and (t-last).total_seconds()>GAP_H*3600: groups.append(cur); cur=[]
    cur.append(p); last=t
if cur: groups.append(cur)

def span_days(g): return (cdate(g[-1])-cdate(g[0])).days
def home_frac(g):
    geo=[p for p in g if ishome(p) is not None]
    return (sum(1 for p in geo if ishome(p))/len(geo)) if geo else None

HOME_CITIES={'auckland','tauranga','mount maunganui'}
def dominant_locality(g):
    locs=collections.Counter(filter(None,(locality(p) for p in g)))
    return locs.most_common(1)[0][0] if locs else None
trips=[]
for g in groups:
    if len(g)<MIN: continue
    hf=home_frac(g)
    if (hf is not None and hf>=0.6) or span_days(g)>30: continue
    dl=dominant_locality(g)
    if dl and dl.strip().lower() in HOME_CITIES: continue   # NZ home area -> daily life, skip
    trips.append(g)

def clean(s): return re.sub(r'[\\/:]', '-', s).strip()
def album_name(g):
    s,e=cdate(g[0]).date(),cdate(g[-1]).date()
    locs=collections.Counter(filter(None,(locality(p) for p in g)))
    place=clean(locs.most_common(1)[0][0]) if locs else None
    datepart=f"{s}" if s==e else f"{s}_{e}"
    return f"{datepart} {place}" if place else f"{datepart}"

trip_albums=[]
used=set()
for g in sorted(trips,key=lambda g:cdate(g[0])):
    nm=album_name(g)
    base=nm; i=2
    while nm in used: nm=f"{base} ({i})"; i+=1
    used.add(nm)
    trip_albums.append({"name": nm, "folder": "Trips",
                        "uuids": [p['uuid'] for p in g],
                        "count": len(g)})

# ---- date-cruft albums to dissolve (by uuid) ----
db=PhotosDB()
dissolve=[]
for a in db.album_info:
    path='/'.join(list(a.folder_names)+[a.title])
    if datey(a.title) or all(datey(x) for x in path.split('/') if x):
        dissolve.append({"uuid": a.uuid, "title": a.title, "path": path, "count": len(a.photos)})

# ---- backfill (tiny) ----
def bogus(p):
    t=cdate(p)
    return (not t) or t.year<2005 or (t.month==1 and t.day==1 and t.hour==0)
backfill=[]
for p in nh:
    if organized(p) or not bogus(p): continue
    yy=mm=None
    for path in apaths(p):
        comps=path.split('/')
        ys=[c for c in comps if YEAR.match(c.strip())]; ms=[c for c in comps if MON.match(c.strip())]
        if ys: yy, mm = ys[-1], (ms[-1] if ms else '01')
    if yy: backfill.append({"uuid": p['uuid'], "date": f"{yy}-{int(mm):02d}-01"})

plan={"trip_albums": trip_albums, "dissolve_albums": dissolve, "backfill": backfill}
json.dump(plan, open(f"{SCRATCH}/reorg_plan.json","w"), indent=1)

with open(f"{SCRATCH}/reorg_names.txt","w") as f:
    f.write(f"TRIP ALBUMS ({len(trip_albums)}) -> all under 'Trips/' folder:\n")
    for t in trip_albums: f.write(f"  {t['count']:4d}  Trips/{t['name']}\n")
    f.write(f"\nDISSOLVE ({len(dissolve)} date albums):\n")
    for a in sorted(dissolve,key=lambda x:-x['count']): f.write(f"  {a['count']:5d}  {a['path']}\n")

print(f"trip albums: {len(trip_albums)} ({sum(t['count'] for t in trip_albums)} photo-adds)")
print(f"dissolve albums: {len(dissolve)} ({sum(a['count'] for a in dissolve)} memberships)")
print(f"date backfill: {len(backfill)} photos")
print(f"\nwrote reorg_plan.json + reorg_names.txt")
print("\n--- first 25 trip album names ---")
for t in trip_albums[:25]: print(f"  {t['count']:4d}  Trips/{t['name']}")
