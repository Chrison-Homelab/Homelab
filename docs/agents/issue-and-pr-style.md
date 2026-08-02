# Issue & PR writing style

The single source of truth for how issues and pull-request descriptions are
written in this repo — by humans and by AI tools alike.

Adapted from [Fallout's version](https://github.com/Fallout-build/Fallout/blob/main/docs/agents/issue-and-pr-style.md),
which [Krautwatch](https://github.com/Chrison-dev/Krautwatch/blob/main/docs/agents/issue-and-pr-style.md)
also follows. Where this repo differs, it says so under
[Homelab-specific](#homelab-specific).

Goal: **terse, scannable, human-readable.** A busy maintainer should get the
point on the first screen, on a phone, without scrolling.

## Principles (apply to issues and PRs)

- **Lead with the ask in one line.** First sentence = what and why. Everything
  else is support.
- **Match length to substance.** A one-line fix gets a one-line description.
  There is no minimum length to hit.
- **Cut filler.** No preamble, no restating the title, no hedging, no marketing
  tone ("elegant", "robust", "seamlessly"), no emoji section headers.
- **Write plainly.** One idea per sentence. No idioms — "blast radius",
  "grace period", "shallow by design" don't travel. Say what you mean:
  "affects fewer guests", "temporary fallback", "handles the common case only".
- **Prefer short, common words.** "use" over "leverage", "keep" over "preserve",
  "shows" over "surfaces", "fixes" over "remediates".
- **Bullets over prose** for anything enumerable.
- **Link, don't recap.** Reference issues (`#123`), PRs, docs, and code
  (`path/to/file.cs:42`) instead of pasting them.
- **Spell out cross-references.** Not `#340` alone — `#340 (the NAS move)`.
  This repo has hundreds of issues and the numbers mean nothing on their own.
- **Describe outcomes, not your process.** What changed and why it matters, not
  the journey.
- **Cut what the reader can get elsewhere.** If the diff or a linked issue
  carries it, reference it. Keep only what the reader can't get without you.
  This is the best single test for whether a line earns its place.
- **It's probably just an issue.** Reserve ADR framing for genuinely
  cross-cutting decisions that need a durable record — see [`docs/adr/`](../adr/).

## Issue shape

```markdown
### Problem
<1–2 sentences: what's wrong or missing, and for whom>

### Outcome
<what "done" looks like — observable behaviour, not implementation>

### Acceptance criteria
- [ ] <testable>
- [ ] <testable>
```

Optional `### Notes` (≤3 lines) for links or constraints. **Drop any section
that doesn't apply** rather than padding it.

## PR shape

```markdown
<one line: what this PR does and why>

### What changed
- <short bullet — not a file-by-file diff narration>

### Verification
<what you actually ran>

Closes #<issue>
```

- **Title is an imperative sentence.** No `feat(scope):` prefix, no trailing
  `(#123)`. The label carries the type; the body carries the link.
  - ❌ `feat(media): plex provisioner + resource headroom (#332)`
  - ✅ `Add a Plex provisioner and raise its resource headroom`
- **Link the issue** (`Closes #123`, or `Part of #123` for one PR in a series).
  Summarize the need in a line — don't recite the issue back.
- **Label at creation time**, in the same `gh pr create` call. See
  [labels](#labels).
- **Keep the `### Verification` line.** In an infrastructure repo this is the
  most valuable part of the description: it is the bit the diff cannot show. A
  shape that validates is not the same as a converge that applied.
- **Don't** restate the title, paste large log blocks, recount your process, or
  enumerate every touched file.

## Labels

[`.github/release.yml`](../../.github/release.yml) is the source of truth for
the changelog categories. Apply **one** category label per PR:

| label | when |
| --- | --- |
| `breaking-change` | removes/renames something others depend on — a shape's `ctid`, an engine CLI flag, a converge contract |
| `enhancement` | a new capability |
| `bug` | fixes incorrect behaviour |
| `security` | fixes a vulnerability or hardens a security-sensitive surface |
| `dependencies` | version bumps |
| `documentation` | docs, comments, ADRs, agent instructions only |
| `skip-changelog` | housekeeping with no consumer-facing note |

Repo-specific labels (`iac`, `networking`, `proxmox`, `unifi`, `synology`,
`hardware`, `ci-cd`, `cleanup`, `blocked`, `priority:*`) are **additive** — use
them freely alongside the category, they don't affect changelog grouping.

Two traps, both already hit in Krautwatch:

- `dependencies` and `skip-changelog` are **excluded** from generated notes. A
  PR carrying `security` **and** `dependencies` disappears entirely, because
  exclusion beats category. A dependency bump that fixes a CVE gets `security`
  alone.
- Infrastructure work is usually `skip-changelog`, but not always. If a user can
  feel it, it earned a real category.

## Homelab-specific

Four things differ from Fallout, and they are deliberate:

1. **No release notes today.** This repo publishes none, so `.github/release.yml`
   is adopted for the label taxonomy and future-proofing, not because anything
   generates notes yet. There are no `target/vCurrent` process labels here.
2. **PRs are created ready, not draft.** Fallout defaults to `--draft`. Here the
   author reviews and merges immediately, so a draft is friction. Use `--draft`
   only when you genuinely want to park something.
3. **Stack submodules come in pairs.** A change under `stacks/<Name>/` is a PR in
   the stack repo *and* a pointer-bump PR here (see
   [ADR-0008](../adr/ADR-0008-stack-submodules.md)). Convention:
   - stack repo PR: the real title, real category label
   - superproject PR: `Bump the <Name> submodule — <what changed>`, labelled
     `skip-changelog` unless the change is user-facing
   - link them both ways
4. **Verification means live state, not CI.** CI validates shapes; it does not
   prove a converge applied or a service came back. Say what you checked against
   the running cluster.

Existing merged PRs are **not** being retitled. Krautwatch had to, because its
release notes are generated from titles. This repo generates none, so the value
is in the convention going forward.

## Anti-patterns

| Instead of… | Write… |
| --- | --- |
| `feat(media): plex provisioner + resource headroom (#332)` | `Add a Plex provisioner and raise its resource headroom` |
| Three paragraphs restating the title | One line, then bullets |
| Pasting a full converge log inline | Link the run, or collapse in `<details>` |
| "As part of this work, I also…" | A second bullet, or a second PR |
| "Fixed the thing from #340" | "Fixed the stale NFS mount left by #340 (the NAS move)" |
| "Tests pass" | "Converge applied; 12/12 storages active; Plex 200 on `:32400`" |
