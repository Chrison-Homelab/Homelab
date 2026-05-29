# Plan: BL-013 — Deploy into Proxmox via community-scripts over SSH

**Backlog:** [BL-013](../Backlog.md#bl-013--provision-via-community-scripts-over-ssh) ·
**Relates to:** [BL-010 Converge](../Backlog.md), [ADR-0001](../adr/ADR-0001-iac-tooling.md),
[iac-csharp-native plan](iac-csharp-native.md)
**Status:** Implemented + live-verified — 2026-05-29. Renderer built; smoke test
deployed CT 3099 to hpe-01 over SSH, verified running via MCP, then destroyed.
One fix surfaced: `TERM=xterm` is needed in the remote env (community-scripts
call `clear`, which errors over a no-TTY SSH session) — now in the renderer.

## Goal

A reproducible, **unblocked** way to deploy LXCs into Proxmox: read a
`shape.yaml`, render a [community-scripts.org](https://community-scripts.org/)
automated-mode invocation, and run it over SSH on the target node. This is the
foundation every future project sits on, and it needs **none** of the
currently-blocked C#/Fallout toolchain (see [Fallout CI block](../Backlog.md#bl-007)).

It is also the ADR/plan-sanctioned **create** mechanism: community-scripts for
bring-up, ProxmoxSharp for config + lifecycle later. The shape contract is the
stable interface — swapping the engine later doesn't change shapes.

## Confirmed by research (2026-05-29)

- **Cluster reachable** — `hpe-01`, `nuc-01`, `desktop-01` all online (Proxmox MCP, read-only).
- **Non-interactive trigger** — `misc/build.func:3108` does `CHOICE="${mode:-${1:-}}"`.
  Setting **`mode=generated`** skips the whiptail menu entirely (no TTY required):
  it hits `case generated)` → `METHOD="generated"; base_settings; echo_default`,
  consuming the exported `var_*`. This is the crux for SSH-with-no-TTY execution.
- **Variable surface** (from `build.func`, precedence `ENV var_* > default.vars > built-ins`):
  `var_ctid var_hostname var_cpu var_ram var_disk var_unprivileged var_brg var_net
  var_gateway var_vlan var_ipv6_method var_mtu var_ns var_searchdomain
  var_container_storage var_template_storage var_os var_version var_tags var_ssh
  var_ssh_authorized_key var_mount_fs var_fuse var_tun var_nesting …`
- The Backlog's earlier `mode=generated var_ctid=…` sketch was **right on the trigger**,
  wrong on some var names (it's `var_net`/`var_container_storage`, not `ipv4`/`storage`).

## Shape → community-scripts var mapping

| shape field              | var                       | notes |
|--------------------------|---------------------------|-------|
| `spec.node`              | — (SSH target host)       | picks which node to `ssh root@…` |
| `spec.app` **(new)**     | — (selects `ct/<app>.sh`) | which community-script to curl |
| `spec.ctid`              | `var_ctid`                | |
| `spec.cores`             | `var_cpu`                 | |
| `spec.memory`            | `var_ram`                 | MB |
| `spec.disk`              | `var_disk`                | GB |
| `spec.storage`           | `var_container_storage`   | |
| `spec.templateStorage`   | `var_template_storage`    | |
| `spec.network.vlan`      | `var_vlan`                | |
| `spec.network.ipv4`      | `var_net`                 | `dhcp` or `CIDR` |
| `spec.network.ipv6`      | `var_ipv6_method`         | `auto`/`dhcp`/`static`/`none` |
| `metadata.name`          | `var_hostname`            | |
| `metadata.tags`          | `var_tags`                | joined with `;` |
| `spec.unprivileged` **(new)** | `var_unprivileged`   | default `1` |
| `spec.os` **(new)**      | `var_os`                  | default `debian` |
| `spec.osVersion` **(new)** | `var_version`           | |
| `spec.mounts[].nfs`      | **not a var** → post-create | host-level mount; out of scope v1 |

## Deliverables

1. **Schema** — `Infrastructure/schema/shape.schema.json`
   - Add `spec.app` (**required** for `kind: LXC`) — the community-script slug.
   - Add optional `spec.unprivileged`, `spec.os`, `spec.osVersion`.
   - Update `examples/servarr.lxc.yaml` (`app: docker`).

2. **Renderer** — `Infrastructure/deploy/Deploy-Shape.ps1` (PowerShell — fits
   Chris's PS/C# ecosystem, runs from hub & CI; `powershell-yaml` for parsing).
   - Params: `-ShapePath <file>`, `-Apply` (default off = **dry-run**), `-Node <override>`.
   - Parse + validate (`kind: LXC`, required fields), map → `var_*`.
   - Build the exact invocation:
     ```
     mode=generated var_ctid=3000 var_hostname=servarr var_cpu=2 var_ram=2048 \
       var_disk=16 var_unprivileged=1 var_brg=vmbr0 var_vlan=1010 var_net=dhcp \
       var_ipv6_method=none var_container_storage=local-lvm var_template_storage=local \
       var_os=debian var_version=12 var_tags='arr-stack;homelab' \
       bash -c "$(curl -fsSL https://github.com/community-scripts/ProxmoxVE/raw/main/ct/docker.sh)"
     ```
   - **Dry-run (default):** print the command, do not execute.
   - **`-Apply`:** `ssh root@<node> '<invocation>'`.

3. **Docs** — `Infrastructure/deploy/README.md`: mechanism, the mapping table,
   dry-run-by-default guardrail, the mounts/post-create gap, idempotency caveat.

4. **Backlog/plan** — tick BL-013 items, link this plan; note the create-mechanism
   decision on BL-010.

## Guardrails (ADR / Overseer conventions)

- **Plan before apply** — renderer is **dry-run by default**; `-Apply` is required
  to mutate. Mirrors the engine's eventual `plan`/`apply` split.
- **Read before write** — ctids/vlans are grounded in already-discovered state.
- **Create-only + existence guard (v1)** — community-scripts create is **not
  idempotent** (re-run on an existing CTID errors/duplicates). Before `-Apply`,
  check whether the CTID already exists (Proxmox MCP / `pvesh`) and refuse if so.
  Update/destroy lifecycle is ProxmoxSharp's job (BL-010), not this renderer.

## Testing

- Render dry-run for `servarr.lxc.yaml`; eyeball the emitted command against the
  confirmed var names.
- **Optional live smoke test (opt-in, Chris's go-ahead only):** deploy a throwaway
  Debian CT at a high CTID (e.g. `3099`) to `hpe-01`, confirm via MCP it came up,
  then destroy it. The default plan stops at dry-run + existence-guard; the live
  mutation is explicitly opt-in.

## Out of scope (v1)

- NFS / host-level mounts (post-create, host-level per CLAUDE.md) — follow-up.
- update / destroy lifecycle → ProxmoxSharp (BL-010).
- C# engine → blocked on the Fallout NuGet channel.
- SynoSharp / NAS and Unifi tracks.
