"""Scan: hash every image in the backup, cache (path,size,phash) to JSONL.
Slow part (SMB + decode). Re-runnable; the fast plan step consumes the cache."""
import sys, os, json, time
from PIL import Image
import pillow_heif
pillow_heif.register_heif_opener()
import imagehash

ROOT = sys.argv[1]
OUT = sys.argv[2]
EXTS = {'.jpg','.jpeg','.png','.heic','.heif','.gif','.bmp','.tiff','.webp'}

# resume support: skip paths already in OUT
done = set()
if os.path.exists(OUT):
    for line in open(OUT):
        try: done.add(json.loads(line)['path'])
        except Exception: pass
print(f"already cached: {len(done)}", flush=True)

files = []
for dp, _, fns in os.walk(ROOT):
    for fn in fns:
        if os.path.splitext(fn)[1].lower() in EXTS:
            p = os.path.join(dp, fn)
            if p not in done:
                files.append(p)
print(f"to hash: {len(files)}", flush=True)

t0 = time.time(); errs = 0
with open(OUT, 'a') as out:
    for i, f in enumerate(files):
        try:
            sz = os.path.getsize(f)
            with Image.open(f) as im:
                h = str(imagehash.phash(im.convert('RGB')))
            out.write(json.dumps({"path": os.path.relpath(f, ROOT), "size": sz, "phash": h}) + "\n")
        except Exception as e:
            errs += 1
            out.write(json.dumps({"path": os.path.relpath(f, ROOT), "error": str(e)[:80]}) + "\n")
        if i and i % 1000 == 0:
            out.flush()
            print(f"  {i}/{len(files)}  ({time.time()-t0:.0f}s, errs={errs})", flush=True)
print(f"DONE: hashed {len(files)-errs}, errors {errs}, {time.time()-t0:.0f}s", flush=True)
