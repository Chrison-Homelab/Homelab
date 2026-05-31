# ADR-0002 — Synology IaC: SSH-runner + thin read-API (not codegen)

- **Status:** Accepted
- **Date:** 2026-05-31
- **Deciders:** Chris
- **Relates to:** [ADR-0001](ADR-0001-iac-tooling.md), issue #57

## Context

We want the Synology NAS (DS1813+) under reproducible IaC — shared folders, NFS
exports, users/groups, network, packages — the same way ProxmoxSharp brought
Proxmox under control. The obvious move is "SynoSharp": a C# client codegen'd
from a published API, mirroring ProxmoxSharp's Route A.

A spike (issue #57, 2026-05-31) showed that doesn't work:

- **No settings/deploy API exists.** DSM's official Web API covers auth + apps
  (FileStation, etc.) only. System-admin endpoints (`SYNO.Core.Share`, `User`,
  `Network`, `Security.Firewall`, `Package`) exist over `/webapi/entry.cgi` but
  are **undocumented, internal, and version-fragile**. There is **no published
  schema** → nothing to code-generate from.
- **What actually works is SSH + on-box CLI.** Synology documents six admin
  commands (`synouser`, `synogroup`, `synoshare`, `synonet`, `synoservice`,
  `synowin`); `synowebapi --exec` fills gaps (runs internal `SYNO.Core.*` as
  root). Every working `community.synology` Ansible module is just an SSH
  wrapper over these — which is also why we retired our Ansible DSM playbooks.
- **Constraints:** DS1813+ is **EOL at DSM 7.1.1-42962** → we target DSM 7.1.
  **NFS exports** are the weak point (not in the documented CLI; only via
  undocumented `SYNO.Core.Share` / `/etc/exports`). `syno*` commands are **not
  idempotent**.

## Decision

**Build SynoSharp as an SSH/agent-runner, not a codegen'd API client.**

1. **Mutations** (shares, NFS, users, groups, network, packages) run via
   **SSH-exec** of the on-box `syno*` CLI + `synowebapi --exec` for gaps. The
   C# surface mirrors ProxmoxSharp's *ergonomics* (a library + `dotnet tool`
   CLI), but the **transport is SSH, not HTTP**, and there is **no Kiota codegen**.
2. **Read / discover** uses a **thin authenticated Web-API client**
   (`SYNO.API.Auth` → `SYNO.API.Info` → `SYNO.Core.*`) — cleaner and lower-risk
   for inventory/state snapshots.
3. **Read before write.** `syno*` aren't idempotent, so the engine diffs desired
   vs discovered state and only applies changes; destructive flags are explicit.
4. **Pin to DSM 7.1** and snapshot the available API list per build; treat all
   undocumented surface (`synowebapi`, `synopkg`, `synosetkeyvalue`,
   `/etc.defaults`) as version-fragile with an explicit risk register.

## Consequences

**Positive**
- Touches the things that matter (NFS, shares, users) — the only path that can.
- Read/discover slice is low-risk and reuses the discovery/state-snapshot pattern.

**Negative / risks**
- We own a brittle, unsupported surface; DSM builds can change `synowebapi`
  method names / `SYNO.Core.*` params. Mitigated by pinning to 7.1 (the box is
  EOL there anyway) and snapshotting the API list.
- **NFS is highest-risk** — prove that write path first, on the Virtual DSM
  container, before trusting it against the live NAS.
- No native idempotency → we design read-before-write + dry-run.

## Notes

- First step is a **read/discover slice** against the `.containers/dsm` Virtual
  DSM container (no system changes), then validate the NFS write path there.
- Lower priority than UnifiSharp (ADR-0003), which can mirror ProxmoxSharp directly.
