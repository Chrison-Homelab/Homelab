# ADR-0003 — UniFi IaC: UnifiSharp via Route-A codegen + legacy write adapter

- **Status:** Accepted
- **Date:** 2026-05-31
- **Deciders:** Chris
- **Relates to:** [ADR-0001](ADR-0001-iac-tooling.md), issue #46

## Context

We want the UniFi-managed network (Cloud Gateway, UniFi OS) under IaC — VLANs/
networks, firewall, port profiles, WLANs — and eventually to replace the UniFi
MCP with our own client (MCP as fallback), exactly as we did with Proxmox.

A spike (issue #46, 2026-05-31) found UniFi is a good fit for the **ProxmoxSharp
Route-A pattern**, with one caveat:

- **Official UniFi Network Integration API** exists with an **OpenAPI spec built
  into the console** (Settings → Integrations, version-pinned; community mirror:
  `beezly/unifi-apis`). Since it's already OpenAPI, we feed it **straight to
  Kiota** — no `apidoc.js`→OpenAPI converter needed (simpler than ProxmoxSharp).
- **Auth:** a local **`X-API-KEY`** header (UniFi OS) — long-lived, no
  cookie/CSRF, ideal for headless/CI.
- **Caveat — write coverage is partial today.** The official API is read-mostly
  (networks creatable; **firewall rules / port profiles / port-forwards not yet**);
  full write scope is rolling out through 2026. Legacy endpoints
  (`/proxy/network/api/s/<site>/...`) still cover those writes but are
  undocumented and version-brittle.

## Decision

**Build UnifiSharp on Route A (OpenAPI → Kiota), hybridised with a thin legacy
write adapter; package exactly like ProxmoxSharp.**

1. **`UnifiSharp.Api`** — Kiota-generated from the **committed OpenAPI** (pulled
   from the console; `beezly` mirror as fallback). Regenerate-on-build.
   **Version tracks the UniFi Network API release** (e.g. `9.x`).
2. **`UnifiSharp`** — hand-written runtime over `.Api`: `X-API-KEY` auth provider,
   the `/proxy/network/` base, `discover`, and a thin **legacy adapter** for the
   write gaps (firewall rules, port profiles), isolated behind interfaces so it's
   deleted endpoint-by-endpoint as official write scope ships. **Independent SemVer.**
3. **`UnifiSharp.Cli`** — a `dotnet tool` (`unifisharp`) wrapping the lib; replaces
   the MCP for UniFi tasks (MCP as fallback).
4. **Packaging identical to ProxmoxSharp:** NuGet on GitHub Packages, publish on
   `v*` tag + prerelease on push to main; `.Api` versioned to the UniFi API
   release, the library to its own SemVer.
5. **Read-only first / safety:** the live network is production. The initial
   build is **read/discover only** against the live controller; no writes until a
   safe test path exists (see Notes).

## Consequences

**Positive**
- Reuses the entire ProxmoxSharp pipeline (Kiota, regen-on-build, two-project
  versioning, CLI, GitHub Packages) — fast to value.
- Spec is real OpenAPI → no converter to own (simpler than ProxmoxSharp).

**Negative / risks**
- **Write coverage gap** until ~2026 → we depend on the brittle legacy adapter
  for firewall/port writes; keep it narrow and deletion-ready.
- The OpenAPI is **version-pinned and console-hosted** (not a stable public URL)
  → regeneration needs a documented "pull from console" step (or the mirror).
- Two auth paths (API key for official, possibly cookie/CSRF for legacy) add
  surface; mitigate by keeping the legacy adapter minimal.

## Notes

- **Testing:** the live network must not be mutated. Investigate a Docker-based
  UniFi controller for write testing; until one exists, writes stay untested and
  out of scope — we focus on the read/discover client + packaging.
- Reference (not a dependency) for the UniFi-OS auth runtime: `KoenZomers/UniFiApi`.
