import json, collections, re
d=json.load(open("/Users/csimon/photo-dedup/snapshot_final.json"))
nh=[p for p in d if not p.get('hidden')]

def named_person(p):
    return any(n and n!="_UNKNOWN_" for n in (p.get('persons') or []))
def L(p): return set(p.get('labels') or [])
def isWA(p): return bool(re.search(r'wa\d{4}', p.get('original_filename',''), re.I))
def keepery(p):  # things we must NOT flag as junk
    return p.get('favorite') or named_person(p)

FURNITURE={'Furniture','Chair','Table','Cabinet','Couch','Sofa','Bed','Desk','Shelf',
           'Bookcase','Wardrobe','Stool','Bench','Drawer','Coffee Table','Dining Table'}
PRODUCT={'Bottle','Container','Tin','Can','Box','Packaged Goods','Bag','Carton','Jar','Tube','Packaging'}
MEAL={'Meal','Dish','Restaurant','Dining','Plate','Dessert','Breakfast','Coffee'}
NATURE={'Outdoor','Sky','Plant','Grass','Land','Water','Beach','Mountain','Tree'}

cand=[p for p in nh if not keepery(p)]
print(f"non-hidden: {len(nh)} | candidates after excluding favorites + named-person photos: {len(cand)}")

# A) memes / screenshots / docs
screenshots=[p for p in cand if p.get('screenshot')]
docs=[p for p in cand if 'Document' in L(p)]
wa_docs=[p for p in docs if isWA(p)]
print(f"\nA) MEMES/DOCS/SCREENSHOTS")
print(f"   screenshots flagged: {len(screenshots)}")
print(f"   'Document'-labeled:  {len(docs)}   (of which WhatsApp-forwarded: {len(wa_docs)})")

# B) furniture, no people, indoor-ish (not nature-dominant)
furn=[p for p in cand if (L(p)&FURNITURE) and not (L(p)&NATURE)]
print(f"\nB) FURNITURE (no people, not outdoor): {len(furn)}")

# C) products/groceries, not a meal
prod=[p for p in cand if (L(p)&PRODUCT) and not (L(p)&MEAL) and not (L(p)&NATURE)]
print(f"C) PRODUCTS/GROCERIES (no people, not a meal/dish, not outdoor): {len(prod)}")

def show(name, lst, n=10):
    print(f"\n--- {name}: {len(lst)} candidates, sample {min(n,len(lst))} ---")
    for p in lst[:n]:
        labs=','.join(sorted(L(p))[:5])
        print(f"   {p['original_filename'][:30]:30s} | {labs}")

show("A1 WhatsApp+Document (highest-confidence meme/junk)", wa_docs)
show("A2 Document (general)", [p for p in docs if not isWA(p)])
show("A3 Screenshots", screenshots)
show("B Furniture", furn)
show("C Products/Groceries", prod)
