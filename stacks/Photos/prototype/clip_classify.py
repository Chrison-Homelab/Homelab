"""CLIP zero-shot junk classifier. Read-only (reads NAS backup images).
Maps each candidate asset -> its backup file -> CLIP category. Outputs clip_results.jsonl."""
import json, os, re, sys, time
from PIL import Image
import pillow_heif; pillow_heif.register_heif_opener()
import torch, open_clip

NAS = "/Volumes/iCloud Backup Chris/PhotosBackup"
SCRATCH = "/private/tmp/claude-501/-Users-csimon-Github-Chrison-dev-RemoteHarness/eca58170-581a-4088-99c2-a5ac86bbed43/scratchpad"
OUT = "/Users/csimon/photo-dedup/clip_results.jsonl"

# ---- categories: keeper vs junk, multiple prompts each ----
CATS = {
 # JUNK
 "meme":          ["a funny meme with text", "a captioned joke image", "an image with big overlaid text", "a social media post screenshot"],
 "screenshot_doc":["a screenshot of a phone or computer screen", "a scanned document or receipt", "a screenshot of an app or website", "a page of text"],
 "furniture_store":["furniture in a store or showroom", "a piece of furniture for sale", "an IKEA showroom display", "a photo of an empty chair, table or cabinet"],
 "product_grocery":["grocery products or packaged food on a shelf", "a product photo of an item for sale", "packaged goods and bottles", "items in a shopping cart"],
 # KEEPER
 "people":        ["a personal photo of people", "a portrait of a person", "friends or family together", "a selfie"],
 "scene_travel":  ["a travel or landscape photo", "an outdoor nature scene", "a building or landmark", "a city street"],
 "meal":          ["a delicious meal at a restaurant", "a plate of prepared food", "a home-cooked dish"],
 "pet_animal":    ["a photo of a pet", "a cute animal"],
 "event":         ["a party or celebration", "a wedding or special event"],
}
JUNK = {"meme","screenshot_doc","furniture_store","product_grocery"}

def norm(n):
    n=os.path.basename(n).lower(); return re.sub(r'_[0-9a-f]{8,}(?=\.[a-z0-9]+$)','',n)

# ---- candidates from snapshot ----
d=json.load(open("/Users/csimon/photo-dedup/snapshot_final.json"))
def named(p): return any(x and x!="_UNKNOWN_" for x in (p.get('persons') or []))
cands=[p for p in d if not p.get('hidden') and not p.get('favorite') and not named(p)]

# ---- map (normname,size)->relpath from the hash scan we already did ----
f2path={}
for line in open(f"{SCRATCH}/hashes.jsonl"):
    r=json.loads(line)
    if 'phash' in r: f2path.setdefault((norm(r['path']), r['size']), r['path'])

work=[]
for p in cands:
    key=(norm(p['original_filename']), p.get('original_filesize') or 0)
    rel=f2path.get(key)
    if rel: work.append((p['uuid'], os.path.join(NAS, rel)))
print(f"candidates: {len(cands)} | with a backup image to classify: {len(work)}", flush=True)

# ---- CLIP ----
device = 'mps' if torch.backends.mps.is_available() else 'cpu'
model, _, preprocess = open_clip.create_model_and_transforms('ViT-B-32', pretrained='laion2b_s34b_b79k')
model.to(device).eval()
tok = open_clip.get_tokenizer('ViT-B-32')
cat_names=list(CATS)
with torch.no_grad():
    cat_feats=[]
    for c in cat_names:
        t=tok(CATS[c]).to(device)
        f=model.encode_text(t); f/=f.norm(dim=-1,keepdim=True)
        cat_feats.append(f.mean(0));
    cat_feats=torch.stack(cat_feats); cat_feats/=cat_feats.norm(dim=-1,keepdim=True)

out=open(OUT,"w"); t0=time.time(); done=0; errs=0; B=32
batch=[]; meta=[]
def flush_batch():
    global done,errs
    if not batch: return
    x=torch.stack(batch).to(device)
    with torch.no_grad():
        f=model.encode_image(x); f/=f.norm(dim=-1,keepdim=True)
        probs=(100*f@cat_feats.T).softmax(-1).cpu()
    for (uuid,path),pr in zip(meta,probs):
        top=int(pr.argmax());
        out.write(json.dumps({"uuid":uuid,"cat":cat_names[top],"conf":round(float(pr[top]),3),
                              "junk":cat_names[top] in JUNK})+"\n")
        done+=1
    out.flush(); batch.clear(); meta.clear()

for uuid,path in work:
    try:
        with Image.open(path) as im:
            batch.append(preprocess(im.convert('RGB'))); meta.append((uuid,path))
    except Exception:
        errs+=1
    if len(batch)>=B:
        flush_batch()
        if done % 1000 < B: print(f"  {done}/{len(work)} ({time.time()-t0:.0f}s, errs={errs})", flush=True)
flush_batch()
print(f"DONE: classified {done}, errors {errs}, {time.time()-t0:.0f}s -> {OUT}", flush=True)
