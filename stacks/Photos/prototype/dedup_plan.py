"""Plan: consume hashes.jsonl + all.json -> asset-level duplicate groups + keep/remove plan.
Fast & re-runnable. This is the reference spec for the C# engine.

Duplicate grouping = union-find over two edge types among NON-HIDDEN assets:
  NAME edge : identical normalized basename, guarded by (same dims) OR (same byte size)
              OR (video & same capture-day) -- avoids coincidental same-name merges.
  PIXEL edge: identical (non-degenerate) perceptual hash -- catches renamed visual dupes.
Keep-rule per group: keep a FAVORITE if any; else largest byte size; tie-break newest date.
"""
import json, os, re, csv, collections

SCRATCH = "/private/tmp/claude-501/-Users-csimon-Github-Chrison-dev-RemoteHarness/eca58170-581a-4088-99c2-a5ac86bbed43/scratchpad"
VIDEO_EXTS = {'.mp4','.mov','.m4v','.3gp','.avi','.mpg','.mpeg'}

def norm(n):
    n = os.path.basename(n).lower()
    return re.sub(r'_[0-9a-f]{8,}(?=\.[a-z0-9]+$)', '', n)

# ---- assets from metadata ----
assets = json.load(open(f"{SCRATCH}/all.json"))
A = {}
for p in assets:
    if p.get('hidden'):           # scope: non-hidden only
        continue
    A[p['uuid']] = {
        'uuid': p['uuid'],
        'name': p['original_filename'],
        'nname': norm(p['original_filename']),
        'size': p.get('original_filesize') or 0,
        'w': p.get('original_width') or p.get('width') or 0,
        'h': p.get('original_height') or p.get('height') or 0,
        'date': (p.get('date') or '')[:19],
        'day': (p.get('date') or '')[:10],
        'fav': bool(p.get('favorite')),
        'albums': p.get('albums') or [],
        'missing': bool(p.get('ismissing')),
        'video': os.path.splitext(p['original_filename'])[1].lower() in VIDEO_EXTS,
        'phash': None,
    }

# map (nname,size) -> uuids  for file->asset resolution
idx = collections.defaultdict(list)
for u, a in A.items():
    idx[(a['nname'], a['size'])].append(u)

# ---- attach perceptual hashes from scan cache ----
DEGENERATE = {'0'*16, 'f'*16}
hostname_collisions = 0
for line in open(f"{SCRATCH}/hashes.jsonl"):
    r = json.loads(line)
    if 'phash' not in r:
        continue
    key = (norm(r['path']), r['size'])
    for u in idx.get(key, []):
        if A[u]['phash'] is None:
            A[u]['phash'] = r['phash']

hashed = sum(1 for a in A.values() if a['phash'])
print(f"non-hidden assets: {len(A)} | with phash: {hashed}")

# drop degenerate / over-shared phashes from pixel matching (blank/dark images collide)
phcount = collections.Counter(a['phash'] for a in A.values() if a['phash'])
def usable_ph(h):
    return h and h not in DEGENERATE and phcount[h] <= 8   # >8 same hash = likely degenerate

# ---- union-find ----
parent = {u: u for u in A}
def find(x):
    while parent[x] != x:
        parent[x] = parent[parent[x]]; x = parent[x]
    return x
def union(a, b):
    ra, rb = find(a), find(b)
    if ra != rb: parent[ra] = rb

# NAME edges (refined): same name AND (same byte size  OR  same dims & same day
#                        OR video & same day). Same-day guard kills reused IMG_#### names.
byname = collections.defaultdict(list)
for u, a in A.items():
    byname[a['nname']].append(u)
name_edges = 0
for nm, us in byname.items():
    for i in range(len(us)):
        for j in range(i+1, len(us)):
            a, b = A[us[i]], A[us[j]]
            same_dims = a['w'] and a['w'] == b['w'] and a['h'] == b['h']
            same_size = a['size'] and a['size'] == b['size']
            same_day  = a['day'] and a['day'] == b['day']
            vid_day   = a['video'] and b['video'] and same_day
            if same_size or (same_dims and same_day) or vid_day:
                union(us[i], us[j]); name_edges += 1

# PIXEL edges (refined): identical pHash.
#   CONFIDENT (auto)  -> dims DIFFER  (re-export/downscale = true dup)
#   AMBIGUOUS (review)-> dims IDENTICAL & names differ (likely burst/sequential) -> NOT auto-removed
byph = collections.defaultdict(list)
for u, a in A.items():
    if usable_ph(a['phash']):
        byph[a['phash']].append(u)
pixel_edges = 0
review_pairs = []
for h, us in byph.items():
    for i in range(len(us)):
        for j in range(i+1, len(us)):
            a, b = A[us[i]], A[us[j]]
            same_dims = a['w'] and a['w'] == b['w'] and a['h'] == b['h']
            if not same_dims:
                union(us[i], us[j]); pixel_edges += 1
            elif a['nname'] != b['nname']:
                review_pairs.append((us[i], us[j]))

# ---- assemble groups ----
groups = collections.defaultdict(list)
for u in A:
    groups[find(u)].append(u)
dupe_groups = {g: us for g, us in groups.items() if len(us) > 1}

def sortkey(u):
    a = A[u]
    return (a['fav'], a['size'], a['date'])   # max() picks favorite, then largest, then newest

plan_rows = []
removable = 0; reclaim = 0
pixel_only_groups = 0
for g, us in dupe_groups.items():
    keep = max(us, key=sortkey)
    # is this group held together only by pixel edges (all distinct names)? -> flag for scrutiny
    names = {A[u]['nname'] for u in us}
    pixel_only = len(names) == len(us)
    if pixel_only: pixel_only_groups += 1
    for u in us:
        a = A[u]
        action = 'KEEP' if u == keep else 'remove'
        if action == 'remove':
            removable += 1; reclaim += a['size']
        plan_rows.append({
            'group': g[:8], 'action': action, 'uuid': a['uuid'], 'filename': a['name'],
            'size': a['size'], 'dims': f"{a['w']}x{a['h']}", 'date': a['date'],
            'favorite': a['fav'], 'pixel_only_group': pixel_only,
            'albums': '|'.join(a['albums']),
        })

# ---- outputs ----
plan_rows.sort(key=lambda r: (r['group'], r['action'] != 'KEEP'))
with open(f"{SCRATCH}/dedup_plan.csv", 'w', newline='') as f:
    w = csv.DictWriter(f, fieldnames=list(plan_rows[0].keys()))
    w.writeheader(); w.writerows(plan_rows)
remove_uuids = [r['uuid'] for r in plan_rows if r['action'] == 'remove']
json.dump(remove_uuids, open(f"{SCRATCH}/remove_uuids.json", 'w'))

# review pairs (ambiguous same-dim pixel matches) -> separate CSV, NOT auto-removed
# drop any pair already inside an auto dupe-group (covered by name/confident-pixel edges)
rp_rows = []
seen = set()
for x, y in review_pairs:
    if find(x) == find(y):    # already grouped via another edge
        continue
    k = tuple(sorted((x, y)))
    if k in seen: continue
    seen.add(k)
    for u in (x, y):
        a = A[u]
        rp_rows.append({'pair': f"{k[0][:6]}~{k[1][:6]}", 'uuid': a['uuid'], 'filename': a['name'],
                        'size': a['size'], 'dims': f"{a['w']}x{a['h']}", 'date': a['date'],
                        'favorite': a['fav'], 'albums': '|'.join(a['albums'])})
if rp_rows:
    with open(f"{SCRATCH}/dedup_review_pairs.csv", 'w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rp_rows[0].keys())); w.writeheader(); w.writerows(rp_rows)

print(f"\nAUTO duplicate groups: {len(dupe_groups)}  (name_edges={name_edges}, confident_pixel_edges={pixel_edges})")
print(f"REMOVABLE assets (auto, high-confidence): {removable}")
print(f"RECLAIM: {reclaim/1e9:.2f} GB")
print(f"groups protected by a favorite: {sum(1 for g,us in dupe_groups.items() if any(A[u]['fav'] for u in us))}")
print(f"\nMANUAL-REVIEW pairs (same-dim pixel matches, burst risk, NOT auto-removed): {len(rp_rows)//2}")
print(f"\nwrote: dedup_plan.csv ({len(plan_rows)} rows), remove_uuids.json ({len(remove_uuids)} uuids), dedup_review_pairs.csv")
