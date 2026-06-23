"""Fast prompt/gate tuning against cached CLIP embeddings (no re-embedding).
Edit CATS / CONF_MIN / MARGIN and re-run (~15s). Reports bucket counts + examples."""
import json, sys, collections
import numpy as np, torch, open_clip

EMB="/Users/csimon/photo-dedup/clip_embed.npy"; UU="/Users/csimon/photo-dedup/clip_embed_uuids.json"
snap={p['uuid']:p for p in json.load(open("/Users/csimon/photo-dedup/snapshot_final.json"))}

CATS = {
 # ---- JUNK ----
 "meme":           ["an internet meme with caption text","a funny image with large impact-font text overlaid",
                    "a screenshot of a social media post","a reaction image with bold text"],
 "screenshot_doc": ["a screenshot of a phone or computer screen","a scanned paper document or form",
                    "a receipt or an invoice","a page of printed text"],
 "furniture_store":["furniture on display in a store showroom","a price tag on a piece of furniture",
                    "an empty sofa chair or cabinet for sale with no people"],
 "product_grocery":["packaged groceries on a supermarket shelf","a retail product with a price label",
                    "bottles and packaged food products on a shelf"],
 # ---- KEEPERS (absorb false positives) ----
 "people":      ["a personal photo of people","a portrait","friends or family together","a selfie"],
 "vehicle":     ["an airplane or aircraft","an airshow","a car train boat or motorcycle"],
 "scene_travel":["a travel or landscape photo","an outdoor nature scene","a building or landmark","a city street"],
 "nature_animal":["a pet or animal","plants flowers or a garden"],
 "meal":        ["a delicious meal at a restaurant","a plate of prepared food","a home-cooked dish"],
 "event":       ["a party or celebration","a wedding or special event"],
 "object_other":["a close-up photo of an everyday object or belonging","a casual snapshot of a personal item"],
}
JUNK={"meme","screenshot_doc","furniture_store","product_grocery"}
CONF_MIN=float(sys.argv[1]) if len(sys.argv)>1 else 0.5
MARGIN  =float(sys.argv[2]) if len(sys.argv)>2 else 0.15

X=np.load(EMB).astype(np.float32); uuids=json.load(open(UU))
device='mps' if torch.backends.mps.is_available() else 'cpu'
model,_,_=open_clip.create_model_and_transforms('ViT-B-32', pretrained='laion2b_s34b_b79k'); model.to(device).eval()
tok=open_clip.get_tokenizer('ViT-B-32')
names=list(CATS); kidx=[i for i,c in enumerate(names) if c not in JUNK]
with torch.no_grad():
    cf=[]
    for c in names:
        f=model.encode_text(tok(CATS[c]).to(device)); f/=f.norm(dim=-1,keepdim=True); cf.append(f.mean(0))
    cf=torch.stack(cf); cf/=cf.norm(dim=-1,keepdim=True); cf=cf.cpu().numpy()

S=X@cf.T                                   # cosine sims
P=np.exp(100*(S-S.max(1,keepdims=True))); P/=P.sum(1,keepdims=True)   # softmax(100*sim)
top=P.argmax(1); topp=P.max(1)
best_keep=P[:,kidx].max(1)

res=[]
for i,u in enumerate(uuids):
    c=names[top[i]]
    is_junk = (c in JUNK) and (topp[i]>=CONF_MIN) and (topp[i]-best_keep[i]>=MARGIN)
    res.append((u, c if is_junk else "keeper", float(topp[i]), is_junk))

byc=collections.Counter(r[1] for r in res)
njunk=sum(1 for r in res if r[3])
print(f"CONF_MIN={CONF_MIN} MARGIN={MARGIN} | total junk-flagged: {njunk}/{len(res)}")
for c in JUNK: print(f"   JUNK {c:16s} {sum(1 for r in res if r[1]==c):5d}")
print(f"   keeper(+gated-out): {byc['keeper']}")
def lab(u): return ','.join((snap.get(u,{}).get('labels') or [])[:4])
def fn(u): return snap.get(u,{}).get('original_filename','?')[:30]
for c in JUNK:
    ex=[r for r in res if r[1]==c][:8]
    print(f"\n--- {c}: {sum(1 for r in res if r[1]==c)} | sample ---")
    for u,_,cf_,_ in ex: print(f"   {cf_:.2f}  {fn(u):30s} | {lab(u)}")
# save current config's junk uuids per category
out={c:[r[0] for r in res if r[1]==c] for c in JUNK}
json.dump(out, open("/Users/csimon/photo-dedup/clip_tuned_buckets.json","w"))
