"""Dry-run for library reorg:
  1) date-backfill: which date-cruft photos have missing/bogus capture dates (derive from folder)
  2) event clustering: cluster UNORGANIZED photos (only in date-cruft or no album) into trips
Read-only (operates on all.json). Prints a preview; writes nothing to Photos."""
import json, re, collections, datetime as dt

SCRATCH = "/private/tmp/claude-501/-Users-csimon-Github-Chrison-dev-RemoteHarness/eca58170-581a-4088-99c2-a5ac86bbed43/scratchpad"
d = json.load(open(f"{SCRATCH}/all.json"))
nh = [p for p in d if not p.get('hidden')]

YEAR=re.compile(r'^(19|20)\d{2}$'); MON=re.compile(r'^(0?[1-9]|1[0-2])$')
YM=re.compile(r'^(19|20)\d{2}[ _/\-.]?(0?[1-9]|1[0-2])$')
def datey(leaf):
    leaf=leaf.strip(); return bool(YEAR.match(leaf) or MON.match(leaf) or YM.match(leaf))

def album_paths(p):
    al=p.get('albums') or []; folders=p.get('folders') or {}
    return ['/'.join(folders.get(a,[])+[a]) for a in al]

def is_organized(p):
    """in at least one REAL (non-date) album"""
    return any(not datey(path.split('/')[-1]) for path in album_paths(p))

# ---------- 1) date backfill ----------
def parse(s):
    try: return dt.datetime.fromisoformat(s)
    except Exception: return None
def bogus(p):
    s=p.get('date') or p.get('date_original') or ''
    t=parse(s)
    if not t: return True
    y=t.year
    return y<2005 or (t.month==1 and t.day==1 and t.hour==0)  # sentinel-ish

cruft_photos=[p for p in nh if not is_organized(p)]
def folder_date_hint(p):
    """extract YYYY and optional MM from this photo's date-cruft album paths"""
    for path in album_paths(p):
        comps=path.split('/')
        yy=[c for c in comps if YEAR.match(c.strip())]
        mm=[c for c in comps if MON.match(c.strip())]
        if yy:
            return yy[-1], (mm[-1] if mm else None)
    return None, None
need_backfill=[p for p in cruft_photos if bogus(p) and folder_date_hint(p)[0]]
print(f"UNORGANIZED photos (only date-cruft / no album): {len(cruft_photos)}")
print(f"  of those with missing/bogus capture date: {sum(1 for p in cruft_photos if bogus(p))}")
print(f"  ...that have a folder YYYY[/MM] to backfill from: {len(need_backfill)}")

# ---------- 2) event clustering on unorganized ----------
def cdate(p):
    return parse(p.get('date') or p.get('date_original') or '')
clust_in=[p for p in cruft_photos if cdate(p)]
clust_in.sort(key=cdate)

def cluster(photos, gap_hours):
    groups=[]; cur=[]
    last=None
    for p in photos:
        t=cdate(p)
        if last and (t-last).total_seconds() > gap_hours*3600:
            groups.append(cur); cur=[]
        cur.append(p); last=t
    if cur: groups.append(cur)
    return groups

def locality(p):
    pl=p.get('place') or {}; ad=pl.get('address') or {}
    if not ad.get('country'): return None
    city=ad.get('city') or ad.get('sub_locality') or ad.get('sub_administrative_area') or ad.get('state_province')
    return f"{ad['country']}/{city}" if city else ad['country']

for GAP, MIN in [(24,10),(36,8),(48,12)]:
    groups=cluster(clust_in, GAP)
    big=[g for g in groups if len(g)>=MIN]
    covered=sum(len(g) for g in big)
    print(f"\n=== GAP={GAP}h MIN={MIN}: {len(big)} event-albums, cover {covered} photos, "
          f"{len(clust_in)-covered} left loose ===")

# home detection
def ishome(p):
    pl=p.get('place') or {}
    return pl.get('ishome')
homed=[p for p in nh if ishome(p) is True]
homeloc=collections.Counter(filter(None,(locality(p) for p in homed)))
print(f"\nphotos flagged ishome=True: {len(homed)} | home localities: {dict(homeloc.most_common(4))}")

# detailed preview: filter out home/daily-life clusters
GAP, MIN = 36, 8
def home_frac(g):
    geo=[p for p in g if ishome(p) is not None]
    if not geo: return None
    return sum(1 for p in geo if ishome(p)) / len(geo)

groups=[g for g in cluster(clust_in, GAP) if len(g)>=MIN]
def name(g):
    s,e=cdate(g[0]),cdate(g[-1])
    locs=collections.Counter(filter(None,(locality(p) for p in g)))
    place=locs.most_common(1)[0][0] if locs else "(no location)"
    span=f"{s.date()}" if s.date()==e.date() else f"{s.date()}…{e.date()}"
    return f"{span}  {place}", len(g)

def span_days(g):
    return (cdate(g[-1])-cdate(g[0])).days
trips=[]; home_skipped=[]
for g in groups:
    hf=home_frac(g)
    if (hf is not None and hf >= 0.6) or span_days(g) > 30:  # current-home OR residence-length -> skip
        home_skipped.append(g)
    else:
        trips.append(g)
trips.sort(key=lambda g: cdate(g[0]))
print(f"\n=== TRIP albums after home-filter: {len(trips)} (skipped {len(home_skipped)} home/daily-life clusters) ===")
print(f"    trips cover {sum(len(g) for g in trips)} photos")
print(f"\n--- 30 sample TRIP albums ---")
for g in trips[:30]:
    nm,n=name(g); print(f"  {n:4d}  {nm}")
print(f"\n--- biggest TRIP albums (sanity) ---")
for g in sorted(trips,key=len,reverse=True)[:8]:
    nm,n=name(g); print(f"  {n:5d}  {nm}")
print(f"\n--- home/daily-life clusters SKIPPED (stay in timeline, not albums) ---")
for g in sorted(home_skipped,key=len,reverse=True)[:6]:
    nm,n=name(g); print(f"  {n:5d}  {nm}")
