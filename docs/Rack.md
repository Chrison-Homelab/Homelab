# Rack

Physical mounting for the homelab's 19" rack. Companion to [`Devices.md`](Devices.md)
(what the hardware *is*) and [`Network.md`](Network.md) (how it's wired) — this file
covers where it physically sits and what holds it there.

**Last updated:** 2026-08-09 — device dimensions sourced from vendor specs, mounting
system evaluated from the published model documentation. **Nothing is rack-mounted
yet**; this is the plan, not the as-built.

---

## Current state

The rack is a **small 19" rack** and is currently populated by exactly one device that
was designed to be racked. Everything else sits loose.

| Device | Rack status |
|---|---|
| `US 24 PoE 250W` switch | ✅ **Native 1U rackmount** — needs nothing |
| `hpe-01` — HP EliteDesk 800 G2 DM | ❌ loose |
| `nuc-01` — Intel NUC D34010WYK | ❌ loose |
| Cloud Gateway Ultra (UDRULT) | ❌ loose |
| `USW Flex Mini` ×2 | ❌ loose — **both offline**, physical triage pending ([#307](https://github.com/Chrison-Homelab/Homelab/issues/307)) |
| `desktop-01` — Gigabyte B450 ATX tower | ❌ **not rackable** in this scheme — see [Out of scope](#out-of-scope) |

### Rack specification — TO BE MEASURED

These are unknown and gate the parts order. Fill them in before printing anything.

| Property | Value | Why it matters |
|---|---|---|
| Usable height (U) | ❓ | Determines whether the 2U plan below fits alongside the switch |
| **Internal depth (mm)** | ❓ | **The critical one.** The EliteDesk is 177 mm deep before any tray; a shallow wall-rack may not take it |
| Mounting holes | ❓ | Square cage-nut vs threaded vs unthreaded changes the screw hardware |
| Post-to-post width | ❓ | Should be the 450 mm standard, but confirm on a small/budget rack |

---

## What needs mounting

**1U of usable internal height is 44.45 mm.** Every device below clears it — but only
just, and one of them has a taller twin that does not (see the warning).

| Device | Dimensions (W × D × H) | Fits 1U? | Source |
|---|---|:---:|---|
| HP EliteDesk 800 G2 DM 35W | 175 × 177 × **34** mm | ✅ | HP datasheet |
| Intel NUC **D34010WYK** | 116.6 × 112.0 × **34.5** mm | ✅ | [Intel spec](https://www.intel.com/content/www/us/en/products/sku/76978/intel-nuc-kit-d34010wyk/specifications.html) |
| UniFi Cloud Gateway Ultra | 141.8 × 127.6 × **30** mm | ✅ | [Ubiquiti tech specs](https://techspecs.ui.com/unifi/unifi-cloud-gateways/ucg-ultra) |

> ⚠️ **The NUC suffix decides everything.** The `D34010WYK` is **34.5 mm** and fits 1U.
> The `D34010WYK**H**` is **49.5 mm** and *does not* — Intel added exactly 15 mm to that
> variant to fit a 2.5" drive bay. `Devices.md` records ours as a plain `WYK`
> (confirmed 2026-08-08), which is the fitting one. It also records a *2.5" SATA SSD*,
> which would normally imply the H chassis — but the Crucial M550 also shipped in
> **mSATA**, which is what a real `WYK` takes, so there is no contradiction.
> **Verify with a ruler before ordering.** A `WYKH` forces 2U or vertical mounting.

> **G2 vs G3+ chassis:** the EliteDesk 800 Mini enclosure is dimensionally unchanged
> across G2/G3/G4 (175 × 177 × 34 mm). Brackets sold for "G3 and newer" are therefore
> *likely* to fit our G2 — but the G2-specific insert below exists precisely because
> the author couldn't find one, so prefer it.

---

## Chosen system — OpenRack 1U

[**OpenRack 1U – A Modular Server Rack System**](https://makerworld.com/en/models/1032069-openrack-1u-a-modular-server-rack-system)
by *Sparco*. Selected because it is a genuine carrier-plus-insert system rather than a
set of one-off brackets, which matches the "buy more mini PCs later" requirement.

**How it works:**

- A **19" 1U base module** provides **two insert slots** (the 10" base has one).
- **Inserts push in and pull out** of the left/right slots — swapping a device is an
  insert swap, not a re-print of the whole face.
- **Blank inserts** are published expressly so people can remix their own.
- **1U inserts are compatible with the 2U base**, so growth doesn't orphan parts.
- Roughly **134 models** in the community insert collection.

Print guidance from the author: the base is profiled for **PLA Basic**; inserts holding
warm equipment are better in **PETG or ABS**.

### Model manifest

Download each from its own page — see [File handling](#file-handling-and-licensing)
before deciding what lands in git.

| Purpose | Model | Designer | Licence |
|---|---|---|---|
| **Base** — 19" 1U, 2 slots | [OpenRack 1U – 19 Inch Base](https://makerworld.com/en/models/1032069-openrack-1u-a-modular-server-rack-system) | Sparco | ⚠️ Standard Digital File License |
| **Insert** — `hpe-01` | [OpenRack 1U HP EliteDesk G2 Insert](https://makerworld.com/en/models/2004801-openrack-1u-hp-elitedesk-g2-insert) | tartineskiller | CC BY-NC-SA |
| **Insert** — Cloud Gateway Ultra | [OpenRack 1U – Ubiquiti UCG Ultra insert](https://makerworld.com/en/collections/4688160-openrack-collection) | TimBim | *unverified* |
| **Insert** — `nuc-01` ⚠️ gap | [OpenRack 1U Intel NUC10FN insert](https://makerworld.com/en/collections/4688160-openrack-collection) | Kodikas | *unverified* |
| **Blank** — remix base for custom inserts | [OpenRack 1U – Blank Insert](https://makerworld.com/en/models/1032228-openrack-1u-blank-insert-remix-it) | Sparco | ⚠️ Standard Digital File License |
| Community insert index | [OpenRack – Collection](https://makerworld.com/en/collections/4688160-openrack-collection) | — | — |

> **The one coverage gap is `nuc-01`.** No insert exists for the gen-4 `D34010WYK`. The
> **NUC10FN** insert is the nearest: its footprint is near-identical (117 × 112 vs
> 116.6 × 112 mm), so it may drop straight in, but the NUC10 is a taller chassis and the
> retention geometry may not line up. Expect either a direct fit or a small remix from
> the blank insert. **This is the part to test-print first.**

### Proposed layout

```
┌─────────────────────────────────────────────────────┐
│ 1U   US 24 PoE 250W                    (native)     │
├──────────────────────────┬──────────────────────────┤
│ 1U   hpe-01 (EliteDesk)  │  nuc-01 (NUC D34010WYK)  │  ← OpenRack base #1
├──────────────────────────┼──────────────────────────┤
│ 1U   Cloud Gateway Ultra │  blank / keystone / Flex │  ← OpenRack base #2
└──────────────────────────┴──────────────────────────┘
```

**2U of new printed hardware**, plus the switch's existing 1U. The second slot of base
#2 is a natural home for a `USW Flex Mini` once [#307](https://github.com/Chrison-Homelab/Homelab/issues/307)
establishes whether they're recoverable; a keystone or blank insert fills it meanwhile.

---

## Alternatives evaluated

### Mauker's Modular 19-inch Rack Mount system

[Collection](https://makerworld.com/en/collections/3016614-modular-19-inch-rack-mounts) ·
[Blanks (the standard)](https://makerworld.com/en/models/770165-19-inch-modular-rack-mount-blanks) ·
~44 models · **CC BY-NC-SA**

A different modularity model: each 1U face is a **Left + Right half-width module** bolted
together, rather than a carrier with sliding inserts.

- **Hardware:** 3× M5 screws (min 12 mm, **16 mm recommended**) + 3× M5 hex nuts. M4 also works.
- **Parts are 4 mm thick**, solid or honeycomb; no supports needed; PLA/PETG/ABS/ASA/Nylon.
- ✅ **Publishes a STEP file** of the blank expressly to make compatible remixes — genuinely
  better than remixing from STL if we end up cutting a custom `nuc-01` module.
- ✅ **CC BY-NC-SA** is a standard, well-understood licence.
- ❌ Weak on mini-PC modules — the catalogue skews to UniFi, TP-Link and switches.

**Worth keeping in view specifically for the NUC gap:** if the NUC10 insert doesn't fit,
authoring a module from Mauker's STEP is easier than remixing OpenRack's STL.

### Commercial, no printing

| Vendor | Product | Note |
|---|---|---|
| [MyElectronics](https://www.myelectronics.nl/us/1u-19-inch-hp-mini-rack-mount-for-2x-hp-mini.html) | 1U 19" mount for **2× HP Mini** | Metal; matches the EliteDesk requirement exactly |
| [racknex](https://racknex.com/shop/hp/) | UM-HPI-201 / 202 | Aluminium HP Mini kits |
| [3drackmounts.com](https://3drackmounts.com/collections/19-rack-mounts) | Printed PETG modular 1U, **ships worldwide** | Already lists a Cloud Gateway Ultra 1U modular. **Sells printed parts only — no STLs** |

Costlier and not modular across future devices, but these sidestep the licensing question
entirely and need no printer.

---

## File handling and licensing

⚠️ **The two licences in play are not the same, and the difference decides what may be
committed.**

**Sparco's base and blank insert** carry a **Standard Digital File License**:

> "You shall not share, sub-license, sell, rent, host, transfer, or distribute in any way
> the digital or 3D printed versions of this object […] (including — but not limited to —
> remixes of this object, and hosting on other digital platforms). The objects may not be
> used without permission in any way whatsoever in which you charge money, or collect fees."

Consequences:

1. **Committing the base STL to git is hosting it on another platform.** This repo is
   private, which makes a "personal storage, not distribution" reading defensible — but it
   is genuinely grey, and it becomes a clear breach the moment the repo goes public.
   **Therefore the base files are gitignored**, and this manifest is the reproducible
   record instead.
2. **Paying a commercial print bureau is a direct conflict** with the fee clause. If we
   don't buy a printer, ask Sparco for permission first — he is active and responsive in
   the model comments.

**Community inserts are mostly CC BY-NC-SA** (confirmed for the EliteDesk G2 insert),
which *does* permit redistribution with attribution and ShareAlike. Those may be committed,
provided the designer is credited in the manifest above. **Verify each insert's licence on
its own page** — it is set per-model, not inherited from the base.

Files live in [`rack/models/`](rack/models/); see that directory's README for the split.

---

## Out of scope

- **`desktop-01`** is a full ATX tower (Gigabyte B450 GAMING X) and cannot be mounted this
  way. It needs a shelf or stays outside the rack. Note it is also the designated sleep
  node ([#191](https://github.com/Chrison-Homelab/Homelab/issues/191)) and the only machine
  with a real PCIe slot ([#334](https://github.com/Chrison-Homelab/Homelab/issues/334)).
- **The three U7LR APs** are ceiling/wall mounted by design.
- **The NAS** (Synology DS1813) is a desktop 8-bay unit, not rack hardware.

---

## Next steps

1. **Measure the rack** — the four unknowns in [Rack specification](#rack-specification--to-be-measured),
   internal depth above all.
2. **Confirm the NUC is a `WYK`** with a ruler (34.5 mm, not 49.5 mm).
3. **Resolve the licence question** — buy a printer, or get Sparco's written OK for
   bureau printing.
4. **Test-print the NUC insert first.** It is the only unproven part; everything else has
   a device-specific model already.
5. Print base ×2 + the EliteDesk, UCG-Ultra and NUC inserts; fill the spare slot with a
   blank or keystone.
6. Update this file with as-built photos and the final U assignments.
