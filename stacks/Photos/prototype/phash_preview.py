import sys, os, re, json, collections, time
from PIL import Image
import pillow_heif
pillow_heif.register_heif_opener()
import imagehash

ROOT = sys.argv[1]
EXTS = {'.jpg','.jpeg','.png','.heic','.heif','.gif','.bmp','.tiff','.webp'}

def norm(p):
    n = os.path.basename(p).lower()
    return re.sub(r'_[0-9a-f]{8,}(?=\.[a-z0-9]+$)', '', n)

files = []
for dp, _, fns in os.walk(ROOT):
    for fn in fns:
        if os.path.splitext(fn)[1].lower() in EXTS:
            files.append(os.path.join(dp, fn))
print(f"image files found: {len(files)}", flush=True)

hashes = collections.defaultdict(list)
sizes = {}
errs = 0
t0 = time.time()
for i, f in enumerate(files):
    try:
        with Image.open(f) as im:
            h = imagehash.phash(im.convert('RGB'))
        hashes[str(h)].append(f)
        sizes[f] = os.path.getsize(f)
    except Exception:
        errs += 1
    if i and i % 1000 == 0:
        print(f"  hashed {i}/{len(files)}  ({(time.time()-t0):.0f}s, errs={errs})", flush=True)

groups = {k: v for k, v in hashes.items() if len(v) > 1}

# Separate: same-name (already caught by name-based tiers / album-mirror copies)
# vs cross-name (NEW duplicates name-matching cannot see)
cross_groups = 0; cross_extra = 0; cross_bytes = 0
samename_extra = 0
examples = []
for k, v in groups.items():
    names = {norm(x) for x in v}
    grp_sizes = sorted(sizes[x] for x in v)
    if len(names) > 1:
        cross_groups += 1
        cross_extra += len(v) - 1
        cross_bytes += sum(grp_sizes[:-1])  # all but largest
        if len(examples) < 12:
            examples.append([(os.path.relpath(x, ROOT), sizes[x]) for x in
                             sorted(v, key=lambda x: -sizes[x])])
    else:
        samename_extra += len(v) - 1

print("\n===RESULT===")
print(json.dumps({
    "hashed": len(files) - errs,
    "errors": errs,
    "phash_groups_total": len(groups),
    "cross_name_groups": cross_groups,
    "cross_name_removable": cross_extra,
    "cross_name_reclaim_gb": round(cross_bytes / 1e9, 3),
    "same_name_removable": samename_extra,
}, indent=2))
print("\n===EXAMPLES (cross-name visual dupes; first=keep)===")
for g in examples:
    print()
    for rel, s in g:
        print(f"  {s/1024:8.0f} KB  {rel}")
