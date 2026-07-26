# ADR-0010 — GitHub repo settings convention across the homelab org (rebase-default merges, codified)

- **Status:** Accepted
- **Date:** 2026-07-26
- **Deciders:** Chris
- **Relates to:** [#296](https://github.com/Chrison-Homelab/Homelab/issues/296) (settings drift),
  [#295](https://github.com/Chrison-Homelab/Homelab/issues/295) (Actions fallout from the same
  migration), [#282](https://github.com/Chrison-Homelab/Homelab/issues/282) (org migration),
  [ADR-0008 stack extraction / meta-repo](ADR-0008-stack-extraction-meta-repo.md) (why there are
  N repos to keep aligned at all).

## Context

The homelab is a meta-repo plus **seven** stack submodule repos (ADR-0008), all private on the
free tier — so **GitHub branch protection is unavailable** and conventions are enforced
client-side (the Husky.NET `pre-push` hook) plus by repo *settings*.

Those settings had silently diverged. Surveyed 2026-07-26, after the org migration (#282):

| Repo | squash | rebase | merge commit | delete on merge |
|---|---|---|---|---|
| `Homelab` | **false** | true | false | true |
| `Homelab.Stacks.DevOps` | true | false | false | true |
| `Homelab.Stacks.SmartHome` | true | true | **true** | **false** |
| `Homelab.Stacks.BuildLab` | true | true | **true** | **false** |

Three different configurations across four repos, and none of them matched what `CLAUDE.md`
claimed ("all homelab repos squash-merge with auto-delete"). Two concrete consequences:

- **`Homelab` had squash disabled entirely**, so the documented merge command simply failed:
  `gh pr merge 294 --squash` → *"Squash merges are not allowed on this repository."* Discovered
  only when a merge was attempted.
- **SmartHome and BuildLab never had settings applied at all** — they were created during the
  stack extraction (#280/#281) and sit at GitHub defaults, which allow merge commits and never
  delete merged branches. `Homelab` itself accumulated **42 stale remote branches** before a
  prune; that is precisely what `delete_branch_on_merge` prevents.

The root cause is structural, not a one-off: **a new GitHub repo starts at defaults**, and this
project creates a new repo every time a stack is extracted. Any convention that lives only in
prose will drift again on the next extraction.

### Merge strategy: the real axis is granularity, not linearity

The stated goal was "linear history". Worth being precise, because it changes the decision:
**squash and rebase both produce linear history** — neither creates a merge commit. What actually
differs is granularity:

- **Squash** — one commit per PR. Trivial to revert; the PR's internal narrative is lost. The
  #294 merge collapsed four deliberately-separated commits (feature / live-testing fixes / ssh
  keepalives / docs) into one.
- **Rebase** — every branch commit is replayed onto `main`. Preserves that narrative and keeps
  `git bisect` sharp, but any WIP or fixup commit lands on `main` permanently.

Neither is right for every PR, and the difference only matters for multi-commit PRs.

## Decision

**Codify one settings convention for every repo in the org, and make it executable rather than
documented.**

1. **Merge strategy: rebase by default, squash as the escape hatch, merge commits never.**
   ```
   allow_rebase_merge:     true    ← default; PRs whose commits each tell part of the story
   allow_squash_merge:     true    ← escape hatch; collapse a WIP/fixup-heavy branch
   allow_merge_commit:     false
   delete_branch_on_merge: true
   ```
   The author picks per PR. `main` is linear under both, so the choice is purely about whether
   the branch's commits are worth keeping — which only the author knows.

2. **The convention is a script, not a paragraph.**
   [`scripts/align-repo-settings.sh`](../../scripts/align-repo-settings.sh) applies it to every
   repo in the org, idempotently. Run it after creating any repo. This follows the same
   build-the-tooling instinct as the rest of the repo — the alternative (a checklist item) is
   what produced the drift being fixed here.

3. **`CLAUDE.md` is layered, not copied.** The superproject holds the shared rules; each stack
   repo gets a **thin** `CLAUDE.md` that references them and adds only what is genuinely local
   (its CTID range, VLAN, members, quirks). Full copies would be N things to keep in sync — the
   same failure mode as the settings drift. No stack repo had one before this ADR.

4. **Archived repos are out of scope, by definition.** An archived repo is read-only, so every
   settings PATCH returns `403 Repository was archived`. That is *correct* state for a retired
   stack, not drift — the sweep skips them rather than reporting a failure. Currently archived:
   `Homelab.Stacks.Komodo` (the placeholder this project drops per ADR-0009) and
   `Homelab.Stacks.ServArr` (the retired media fleet). Neither gets a `CLAUDE.md` either.

### Applied state (2026-07-26)

Sweep result after adopting this ADR — 6 live repos aligned, 2 archived skipped:

```
Homelab · Azure · BuildLab · DevOps · ErpForFactoryGames · SmartHome   → aligned
Komodo · ServArr                                                       → SKIP (archived)
.github                                                                → SKIP (org profile)
```

## Consequences

- **+** One command re-aligns the whole org; a new stack repo is one script run from correct.
- **+** Multi-commit PRs keep their narrative by default, so `git log` on `main` stays useful for
  archaeology (which matters here — commit bodies carry a lot of the why).
- **+** `delete_branch_on_merge` everywhere stops stale-branch accumulation at the source.
- **−** Two permitted strategies is a slightly weaker convention than one; mitigated by naming
  rebase the documented default rather than leaving it to taste.
- **−** Rebase merges put per-commit hygiene on the author: a branch with `fix typo` commits
  should be squashed, not rebased. No tooling enforces that judgement.
- **−** Still client-side. Nothing *prevents* a merge commit if someone changes a setting by
  hand; the script only re-asserts the intended state when run. Branch protection would enforce
  it, but needs a paid plan for private repos.

## Alternatives considered

- **Squash-only (the previous documented rule)** — simplest and trivially revertable, but
  discards the internal narrative of exactly the multi-step PRs this project produces most.
  Rejected as the default; retained as the escape hatch.
- **Rebase-only** — strictest linearity, but every WIP commit lands on `main` with no way to
  collapse a messy branch at merge time. Rejected as too rigid for a solo/agent workflow where
  branches are often iterated live against real hardware.
- **Allow merge commits** — rejected outright; non-linear history for no gain here.
- **Paid plan for branch protection** — the actually-enforcing option, and the only way to make
  any of this non-bypassable. Not worth the cost for a private homelab; revisit if the repos ever
  go public or gain collaborators.
- **Leave settings to the GitHub UI** — status quo; it is what drifted. Rejected.

## Out of scope

- The Actions/runner breakage from the same migration (#295) — separate concern, needs org-admin.
- Branch protection / rulesets, gated on a paid plan.
- The `pre-push` hook's own rules (direct-to-`main`, force-push, merged-PR-branch guards); they
  are unchanged and complementary.
