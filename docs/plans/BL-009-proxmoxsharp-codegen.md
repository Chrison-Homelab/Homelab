# Plan: BL-009 — ProxmoxSharp via schema-driven codegen

**Backlog:** [BL-009](../Backlog.md#bl-009--c-native-iac-discover-read-only-state-import) ·
**ADR:** [ADR-0001](../adr/ADR-0001-iac-tooling.md) ·
**Repo:** `vendor/ProxmoxSharp` (submodule) ·
**Status:** Proposed — 2026-05-30 (M0 explore done; execution route TBD)

## Goal

Generate **most** of ProxmoxSharp's C# from Proxmox's own published API schema,
with only thin hand-written scaffolding (auth, HTTP runtime, the generator
itself). Ship it as a NuGet package the Homelab hub consumes. First real
milestone is the **read path** (nodes / LXC / VM / storage / network) + a
`discover` dump — the BL-009 dogfood goal. Not gated on the blocked Fallout
NuGet channel (it's a plain class library).

## Grounded findings (M0 — 2026-05-30)

- **Schema source:** `/usr/share/pve-docs/api-viewer/apidoc.js` on every node.
  Ours (`hpe-01`, **PVE 9.2.2**) is **4.27 MB**. Also published at
  [pve.proxmox.com/pve-docs/api-viewer/](https://pve.proxmox.com/pve-docs/api-viewer/),
  but that tracks *latest* — **pull from our node** to stay version-matched.
- **Format:** a JS file `const apiSchema = [ … ];` wrapping a JSON tree. Each
  node: `{ path, text, children[], info{ GET|POST|PUT|DELETE: { parameters{properties}, returns, description, method, name, allowtoken, permissions } } }`.
  Strip the `const … =`/`;` wrapper → parse as JSON.
- **Not pure JSON Schema / not OpenAPI:** booleans as `0/1`, custom `format`
  (e.g. `pve-replication-job-id`), `typetext`, `optional` flags, CSV "lists".
  The generator must handle these quirks.
- **`allowtoken`** per method → tells us what an API token can reach (our
  read-only auth path).
- **Existing third-party OpenAPI conversions** (`ramphy/proxmox-api`,
  `akikungz/pve-openapi`, `dheurtev/pve-apidoc-converter`) = **reference only**:
  unmaintained, hand-reverse-engineered, not version-matched to 9.2.2.

## Approach — the fork to decide together

From `apidoc.js` → C#, two routes:

- **Route A — own converter → OpenAPI → Kiota/NSwag → C#.** We own only the
  `apidoc.js`→OpenAPI converter (the messy part); reuse a mature C# emitter.
  Less emitter to maintain; but two transforms, and the OpenAPI step can launder
  away Proxmox-specific detail.
- **Route B — own `apidoc.js` → C# generator (Scriban/T4).** Full control over
  the `0/1`-bool / custom-format / CSV quirks and idiomatic C# naming; one
  transform. We own the whole emitter.

**Leaning B** for the model + typed-path layer (control over quirks + clean
output), atop a thin hand-written runtime. **Decision deferred to execution.**

## Components (all inside `vendor/ProxmoxSharp`)

1. **Pinned schema** — `schema/apidoc.<pve-version>.js` committed (snapshot from
   our node) + a small refresh script. Regen is explicit and diffable.
2. **Generator** — console/MSBuild tool: schema → C# (models + typed path
   clients). Runs on-demand; generated output committed for reviewable diffs.
3. **Runtime (hand-written)** — `ProxmoxClient` over `HttpClient`; API-token auth
   (`Authorization: PVEAPIToken=user@realm!tokenid=secret`); the `{ data: … }`
   envelope; error handling; `0/1`↔`bool` and CSV-list converters. Generated code
   sits on top.
4. **Tests** — generator unit tests + one thin read-only integration test against
   the live cluster.
5. **Packaging** — NuGet (GitHub Packages or local feed) the hub consumes.

## Milestones

- **M0 Explore** — schema located + structure understood. ✓ (this doc)
- **M1 Scaffold** — solution layout (Runtime / Generator / Tests), `.csproj`s, local build.
- **M2 Auth + first manual read** — `ProxmoxClient` + token auth + `GET /nodes`
  verified against the live cluster (no codegen yet).
- **M3 Generator MVP** — parse `apidoc.js` → emit models + read endpoints
  (nodes / lxc / qemu / storage / network).
- **M4 Discover** — `discover` routine dumps structured live state; reconcile vs `/Infrastructure` shapes.
- **M5 Package** — publish; hub consumes.

## Guardrails

- **Read-only first** — token with an audit/read role; no write codegen until the
  read path is solid (write/lifecycle = BL-010).
- **Version-matched schema pinned**; regeneration explicit + diffable.
- **Generated vs hand-written** clearly separated; runtime is reviewed by hand.

## Out of scope / captured separately

- **CLI global tool → [BL-014](../Backlog.md)** — a `dotnet tool`-installable CLI
  wrapping ProxmoxSharp so it's usable directly from Claude. Captured; **not now**.
- Write / lifecycle path → BL-010.

## Execution decision points (discuss before coding)

1. **Codegen route A vs B** (OpenAPI+Kiota vs own emitter).
2. **Packaging target** — GitHub Packages vs local feed (GH Packages shares the
   PAT/auth story with BL-007).
3. **Generator placement** — build-time MSBuild vs committed generated output
   regenerated on-demand (committed output is friendlier for a consumed package
   + diff review).
4. **Target framework** — `net8.0` (LTS) vs `net9.0`.
