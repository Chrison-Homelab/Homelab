# Plan: BL-009 — ProxmoxSharp via schema-driven codegen

**Backlog:** [BL-009](../Backlog.md#bl-009--c-native-iac-discover-read-only-state-import) ·
**ADR:** [ADR-0001](../adr/ADR-0001-iac-tooling.md) ·
**Repo:** `vendor/ProxmoxSharp` (submodule) ·
**Status:** Approved — 2026-05-30. M0 explore done. **Route A** chosen
(apidoc.js → OpenAPI → Kiota → C#), target **net10.0** (current LTS).

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

### Decision (locked 2026-05-30): Route A

**`apidoc.js` → our converter → OpenAPI 3.0 → [Kiota](https://github.com/microsoft/kiota) → C#.**
Reuse Microsoft's maintained Kiota emitter; we own only the version-matched
`apidoc.js`→OpenAPI converter + the auth provider + a thin runtime. Target
**net10.0**.

Pipeline:
```
apidoc.js (PVE 9.2.2, from our node)
   │  ProxmoxSharp.SchemaGen  (our tool: strip JS wrapper, JSON tree → OpenAPI 3.0,
   │                            normalising 0/1 bools, custom formats, CSV lists, optional flags)
   ▼
openapi.json  (committed, diffable)
   │  kiota generate -l CSharp        (pinned via .config/dotnet-tools.json)
   ▼
generated C# request builders + models
   │  + hand-written runtime: PVEAPIToken auth provider, {data:…} envelope, converters
   ▼
ProxmoxSharp  (NuGet the hub consumes)
```

## Components (all inside `vendor/ProxmoxSharp`)

1. **Pinned schema** — `schema/apidoc.<pve-version>.js` committed (snapshot from
   our node) + a small refresh script. Regen is explicit and diffable.
2. **`ProxmoxSharp.SchemaGen`** — our converter tool: `apidoc.js` → OpenAPI 3.0,
   normalising the Proxmox quirks. Output `openapi.json` committed. *(Open
   sub-decision: build from scratch vs. fork an existing apidoc→OpenAPI converter
   — lean build-our-own for version-match + control.)*
3. **Kiota** — pinned as a local `dotnet tool`; a `generate.ps1`/lock file drives
   `openapi.json` → C#. Generated output committed for reviewable diffs.
4. **Runtime (hand-written)** — Kiota `IAuthenticationProvider` for API-token auth
   (`Authorization: PVEAPIToken=user@realm!tokenid=secret`); the `{ data: … }`
   envelope unwrap; `0/1`↔`bool` and CSV-list converters. Generated code sits on top.
5. **Tests** — converter unit tests + one thin read-only integration test against
   the live cluster.
6. **Packaging** — NuGet (GitHub Packages or local feed) the hub consumes (M5).

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

## Execution decision points

1. ~~Codegen route A vs B~~ → **A (OpenAPI + Kiota)**, locked 2026-05-30.
2. ~~Target framework~~ → **net10.0**, locked 2026-05-30.
3. **Generator placement** — committed generated output regenerated on-demand
   (lean: friendlier for a consumed package + diff review). *Open, M3.*
4. **Packaging target** — GitHub Packages vs local feed (GH Packages shares the
   PAT/auth story with BL-007). *Open, M5.*
5. **SchemaGen converter** — build our own vs fork an existing apidoc→OpenAPI
   tool. *Open, M3 (lean build-our-own).*
