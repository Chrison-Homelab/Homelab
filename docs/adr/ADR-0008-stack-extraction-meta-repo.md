# ADR-0008 — Stack extraction & the meta-repo composition model: domain stacks become `Homelab.Stacks.*` submodules

- **Status:** Accepted
- **Date:** 2026-07-20
- **Deciders:** Chris
- **Relates to:** [ADR-0001 IaC tooling](ADR-0001-iac-tooling.md) (establishes "submodules
  declare the *shape*; the hub converges" — this ADR extends that to a first-class
  composition model), Phase 0 epic [#272](https://github.com/Chrison-dev/Homelab/issues/272),
  Leapmotor→SmartHome fold [#271](https://github.com/Chrison-dev/Homelab/pull/271)

## Context

The repo has grown into an ~11-stack monorepo that **mixes concerns**: core homelab
infra (`Core`, `monitoring`, `Media`, `Photos`) sits next to domain/app stacks
(`SmartHome`, `Gaming`, `BuildLab`) and things that aren't homelab infra at all.

We are, however, **already halfway to a split model without having named it**. Five
stacks are already standalone repos linked back as submodules under a
`Homelab.Stacks.<Name>` convention: **Azure, DevOps, Komodo, ErpForFactoryGames,
Infrastructure**. What remains in-tree is a mix of genuinely-core and
ready-to-leave stacks.

Investigation into whether more stacks *can* be split cleanly (the "schema coupling"
worry) found that **it is not a hard blocker**:

- **The engine loads a stack by directory path** — `ShapeLoader.LoadStack(stackDir)`
  reads `stack.yaml` + `*.lxc.yaml` + `*.vm.yaml` from whatever directory it is
  pointed at. A submodule checked out at `stacks/<Name>` is found transparently
  because it is physically present in the superproject tree.
- **The schema is resolved by the engine from its own `Infrastructure/` path.** The
  `../../Infrastructure/schema/shape.schema.json` strings in stack YAML are **doc
  comments**, not a runtime dependency.
- **The existing 5 split stacks are "dumb content"** — no CI, no build entrypoint,
  no schema copy. They are standalone only for history/versioning and are operated
  exclusively from the superproject.
- The `ctidRange` guard described in the schema is **documented but not yet
  engine-enforced**, so out-of-range adopted members (e.g. the HA VM 2000, and now
  `leapmotor-mate` CT 4100) validate today.

So the decision is not *can we* but *should we, and with what rigor*. The trade-off:

- **Costs (ongoing, operational):** a **2-PR tax** per change (stack repo PR → merge →
  superproject pointer bump PR); **submodule drift** (hit live this session — stale
  ProxmoxSharp/SynoSharp pointers on local `main`); and **no independent validation**
  (a bad edit in a stack repo's own PR is only caught when the superproject converges).
- **Benefits (target-specific):** `SmartHome` carries **binary firmware blobs, vendor
  PDFs, and reverse-engineered mTLS certs** — extracting keeps the meta-repo lean and
  isolates third-party/RE material. `BuildLab` is tied to the **Fallout build system**
  (a separate lifecycle and audience). Plus independent history, smaller superproject
  clone, and per-repo access control.

## Decision

**Adopt the meta-repo composition model explicitly, and extract domain stacks into
their own `Homelab.Stacks.<Name>` repos, composed as submodules at `stacks/<Name>`.**

1. **Homelab is the umbrella / composition + converge authority.** The engine runs
   **only** from the superproject, where `Infrastructure/` and every stack submodule
   coexist. Stacks declare shape; the hub converges (per ADR-0001).
2. **Extract `SmartHome` and `BuildLab` next.** `SmartHome` extraction is gated on the
   Leapmotor fold (#271) merging first, so the new repo carries `leapmotor-mate`. Both
   migrate **with history** (`git filter-repo` / subtree split).
3. **Core cross-cutting stacks stay in-tree for now** — `Core`, `monitoring`, `Media`,
   `Photos`. `Media` in particular is the heart of the lab and heavily coupled to
   `monitoring`/`cloudflared`. `Gaming` is a borderline future candidate.
4. **Validation = opt-in shared Action.** Publish `shape.schema.json` as a **versioned
   artifact** from the Infrastructure repo, plus a **reusable GitHub Action** any stack
   repo *may* call to validate its shapes against the pinned schema. Opt-in, not
   mandatory — keeps parity with the existing "dumb content" repos while giving early
   feedback where wanted.
5. **Submodule bumps = auto-bump PR bot.** A scheduled + `workflow_dispatch` Action in
   Homelab runs `git submodule update --remote` and opens a PR bumping stack pointers,
   turning drift into a reviewable PR and removing the manual second PR.

Phase 0 (items 4 + 5, plus the schema publish) is tracked in
[#272](https://github.com/Chrison-dev/Homelab/issues/272) and **unlocks** the
extractions.

### What qualifies a stack for extraction

- **Extract** when it has a distinct lifecycle/audience (BuildLab ↔ Fallout), carries
  bulky or third-party assets that bloat the meta-repo (SmartHome firmware/certs), or
  benefits from independent history/access control.
- **Keep in-tree** when it is core cross-cutting infra or tightly coupled to other
  in-tree stacks (Media ↔ monitoring/cloudflared). Tidiness alone does **not** justify
  paying the 2-PR + drift tax.

## Consequences

**Positive**
- Meta-repo stays lean; binary/RE material lives in the domain repo that owns it.
- Consistent, already-proven pattern (5 stacks) with an explicit rationale and a
  documented extraction criterion.
- Auto-bump bot + opt-in validation remove the two sharp edges (drift, late validation)
  that made the submodule model risky.

**Negative / risks**
- More repos to own; every split stack adds a submodule pointer to keep current.
- The 2-PR tax is reduced, not eliminated — the bot batches bumps but review remains.
- The schema becomes a **published contract**: bumping `shape.schema.json` now has
  downstream consumers, so it needs a version discipline it didn't before.
- Opt-in validation means a stack repo *can* still merge invalid YAML if it declines
  the Action — caught at the superproject converge, as today.

## Notes

- No engine changes are required to extract a stack — the loader already takes a
  directory path and resolves the schema from `Infrastructure/`.
- The `ctidRange` guard remains unenforced; if/when it lands, adopted out-of-block
  members (HA VM 2000, `leapmotor-mate` CT 4100) need an explicit exception flag
  (e.g. a `managed:false` / `adopted:true` marker) rather than range widening.
