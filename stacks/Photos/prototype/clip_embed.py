"""Compute & cache CLIP image embeddings for the candidate set (one expensive pass).
Output: clip_embed.npy (float16 N x 512) + clip_embed_uuids.json (uuid order).
Then prompt/gate tuning is instant against the cache (no re-embedding)."""
import json, os, re, time
import numpy as np
from PIL import Image
import pillow_heif; pillow_heif.register_heif_opener()
import torch, open_clip

NAS="/Volumes/iCloud Backup Chris/PhotosBackup"
SCRATCH="/private/tmp/claude-501/-Users-csimon-Github-Chrison-dev-RemoteHarness/eca58170-581a-4088-99c2-a5ac86bbed43/scratchpad"
OUTV="/Users/csimon/photo-dedup/clip_embed.npy"; OUTU="/Users/csimon/photo-dedup/clip_embed_uuids.json"

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
    rel=f2path.get((norm(p['original_filename']), p.get('original_filesize') or 0))
    if rel: work.append((p['uuid'], os.path.join(NAS, rel)))
print(f"to embed: {len(work)}", flush=True)

device='mps' if torch.backends.mps.is_available() else 'cpu'
model,_,preprocess=open_clip.create_model_and_transforms('ViT-B-32', pretrained='laion2b_s34b_b79k')
model.to(device).eval()

embs=[]; uuids=[]; t0=time.time(); errs=0; B=32; batch=[]; meta=[]
def flush():
    global errs
    if not batch: return
    x=torch.stack(batch).to(device)
    with torch.no_grad():
        f=model.encode_image(x); f/=f.norm(dim=-1,keepdim=True)
    embs.append(f.cpu().half().numpy()); uuids.extend(meta)
    batch.clear(); meta.clear()
for uuid,path in work:
    try:
        with Image.open(path) as im: batch.append(preprocess(im.convert('RGB'))); meta.append(uuid)
    except Exception: errs+=1
    if len(batch)>=B:
        flush()
        if len(uuids)%1024<B: print(f"  {len(uuids)}/{len(work)} ({time.time()-t0:.0f}s, errs={errs})", flush=True)
flush()
arr=np.concatenate(embs); np.save(OUTV, arr); json.dump(uuids, open(OUTU,"w"))
print(f"DONE: {arr.shape} embeddings, errs={errs}, {time.time()-t0:.0f}s", flush=True)
