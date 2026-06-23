"""OCR pass over NAS candidate images (Apple Vision via ocrmac). Read-only.
Per image: total recognized chars + text bbox area fraction. -> ocr_results.jsonl
Meme = meaningful text coverage over a real image (tuned later)."""
import json, os, re, time
from PIL import Image
import pillow_heif; pillow_heif.register_heif_opener()
from ocrmac import ocrmac

NAS="/Volumes/iCloud Backup Chris/PhotosBackup"
SCRATCH="/private/tmp/claude-501/-Users-csimon-Github-Chrison-dev-RemoteHarness/eca58170-581a-4088-99c2-a5ac86bbed43/scratchpad"
OUT="/Users/csimon/photo-dedup/ocr_results.jsonl"
IMG_EXTS={'.jpg','.jpeg','.png','.heic','.heif','.gif','.bmp','.tiff','.webp'}

def norm(n): n=os.path.basename(n).lower(); return re.sub(r'_[0-9a-f]{8,}(?=\.[a-z0-9]+$)','',n)
d=json.load(open("/Users/csimon/photo-dedup/snapshot_final.json"))
def named(p): return any(x and x!="_UNKNOWN_" for x in (p.get('persons') or []))
cands=[p for p in d if not p.get('hidden') and not p.get('favorite') and not named(p)]
f2path={}
for line in open(f"{SCRATCH}/hashes.jsonl"):
    r=json.loads(line)
    if 'phash' in r: f2path.setdefault((norm(r['path']), r['size']), r['path'])
work=[]
for p in cands:
    if os.path.splitext(p['original_filename'])[1].lower() not in IMG_EXTS: continue  # skip video
    rel=f2path.get((norm(p['original_filename']), p.get('original_filesize') or 0))
    if rel: work.append((p['uuid'], os.path.join(NAS, rel)))

done=set()
if os.path.exists(OUT):
    for l in open(OUT):
        try: done.add(json.loads(l)['uuid'])
        except Exception: pass
work=[w for w in work if w[0] not in done]
print(f"to OCR: {len(work)} (already done {len(done)})", flush=True)

t0=time.time(); errs=0
with open(OUT,"a") as out:
    for i,(uuid,path) in enumerate(work):
        try:
            with Image.open(path) as im:
                im=im.convert('RGB')
                anns=ocrmac.OCR(im, framework="vision").recognize()  # [(text, conf, [x,y,w,h]), ...]
            chars=sum(len(t) for t,_,_ in anns)
            area=sum(bb[2]*bb[3] for _,_,bb in anns)   # normalized bbox area, summed
            out.write(json.dumps({"uuid":uuid,"chars":chars,"nblocks":len(anns),"area":round(area,4)})+"\n")
        except Exception as e:
            errs+=1; out.write(json.dumps({"uuid":uuid,"error":str(e)[:60]})+"\n")
        if i and i%500==0:
            out.flush(); print(f"  {i}/{len(work)} ({time.time()-t0:.0f}s, errs={errs})", flush=True)
print(f"DONE: {len(work)-errs} ocr'd, errs={errs}, {time.time()-t0:.0f}s", flush=True)
