# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Personal homelab infrastructure-as-code repository managing Proxmox hypervisors, Synology NAS devices, and associated monitoring/networking. The core philosophy: **infrastructure-as-code first, modular, idempotent, reversible, and minimal UI reliance**. Always assume multi-node and multi-NAS environments.

## ⚠️ Shared external accounts — add-only, never disturb existing setup

Several external accounts/services are **shared with pre-existing, hand-managed
setup that is NOT in this repo** — most notably **Cloudflare** (account + the
**`chrison.dev`** zone) and the GitHub account. When operating on these:

- **Only create/manage resources that *we* add.** Treat everything you did not
  create as read-only and off-limits.
- **NEVER modify, delete, or repurpose existing resources** (DNS records,
  tunnels, zones, firewall/page rules, access apps, API tokens, etc.). Before
  adding anything, **read the current state first** and confirm your new resource
  doesn't collide with an existing one (e.g. check a hostname/record doesn't
  already exist).
- **If the task cannot be done without touching existing config, STOP.** Do not
  proceed. Come back to the user with: (1) clear reasoning, (2) exactly what you'd
  need to change and why, (3) the safest alternative. Wait for explicit approval.
- **Scope credentials minimally.** Request/expect API tokens scoped to only what
  the task needs (e.g. a Cloudflare token limited to the `chrison.dev` zone's DNS
  edit + Tunnel edit), never account-wide. Tokens live in the gitignored
  `secrets.env`, never committed or echoed.
- **Prefer self-contained, removable additions** (e.g. a dedicated cloudflared
  tunnel + LXC for a stack, rather than extending an existing one).

## Proxmox access: prefer the `proxmoxsharp` CLI

For Proxmox read/discovery tasks, **use our own `proxmoxsharp` CLI first** (our
dogfooded client, `vendor/ProxmoxSharp` → installed via `dotnet tool install -g ProxmoxSharp.Cli`).
The `pve` MCP is a **fallback** (use it if the CLI is unavailable or for an
endpoint the CLI doesn't expose yet).

```bash
# All CLIs read config from the environment. The root `secrets.env` (gitignored)
# is the ONE canonical file — it holds every service (Proxmox / Synology / UniFi
# + Cloudflare / GitHub). It is GENERATED from `secrets.env.template` (the committed
# schema) + Bitwarden Secrets Manager — regenerate it on any machine with:
scripts/secrets-sync.sh          # macOS/Linux  (secrets-sync.ps1 on Windows)
# then source it once:
set -a && . ./secrets.env && set +a          # → proxmoxsharp / synosharp / unifisharp all configured
proxmoxsharp discover    # structured ClusterSnapshot (JSON)
proxmoxsharp nodes       # list nodes
proxmoxsharp version     # PVE version
```

Public endpoint (valid TLS): `https://proxmox.chrison.dev/api2/json`. The legacy
node IP `192.168.179.3:8006` uses a self-signed cert (`PROXMOX_VERIFY_TLS=false`).

## Git workflow & guardrails

**PR-only — never commit to `main` directly.** These repos can't use GitHub branch
protection (private + free tier), so the rules are enforced **client-side** by a
**Husky.NET `pre-push` hook** ([`.husky/pre-push`](.husky/pre-push)). All homelab
repos squash-merge with auto-delete of the merged branch (`delete_branch_on_merge`).

Always branch fresh off an up-to-date `main`:

```bash
git fetch origin && git checkout main && git pull --ff-only
git checkout -b feat/<thing>      # or fix/<thing>
```

### Parallel agent sessions → use a `git worktree` (never share one checkout)

Multiple concurrent agent sessions **must not share a single working directory.** A shared
working tree means uncommitted edits and branch switches from one session clobber the other,
and branch discipline alone can't prevent it. Give **each concurrent session its own worktree**:

```bash
git worktree add ../Homelab-<task> -b feat/<thing>   # isolated checkout + fresh branch
git worktree list                                    # see active worktrees
git worktree remove ../Homelab-<task>                # clean up after the PR merges
```

**Per-worktree setup** — a new worktree does **not** inherit the primary checkout's untracked
files or tooling. Before working/pushing in it: regenerate the gitignored `secrets.env`
(`scripts/secrets-sync.sh`) and install hooks (`dotnet tool restore && dotnet husky install`).
The `.containers/*` test stacks are pathed to the primary checkout — run those from there.

**Never reuse an existing feature branch without confirming its PR is still open:**
`gh pr view <branch> --json state` (OPEN / MERGED / CLOSED). `gh pr list --head <branch>`
returning `[]` means *no open PR* — **not** *no PR*; an already-merged branch can still
exist and pushing onto it goes nowhere (how a commit was lost on 2026-06-29).

The `pre-push` hook blocks (bypass: `git push --no-verify`): direct pushes to `main`,
force / non-fast-forward pushes, and pushes onto a branch whose PR is already
MERGED/CLOSED. It auto-installs on `./build.sh`; manual setup is
`dotnet tool restore && dotnet husky install`.

## Stack submodules (meta-repo model, ADR-0008)

Domain stacks live in their own **`Homelab.Stacks.<Name>`** repos, composed here
as submodules under `stacks/<Name>`. The engine is unaffected — it loads a stack
by directory path, so a submodule checkout is discovered exactly like in-tree
content. Converge/validate authority stays in this superproject; stack repos are
independently versioned.

### New stack submodule → grant it the schema secret

Each stack repo runs an **opt-in** `validate.yml` (calls the superproject's
reusable [`_validate-shapes.yml`](.github/workflows/_validate-shapes.yml)), which
downloads the pinned schema/validator from the private Homelab `schema-v1` release.
That download needs the **`SCHEMA_RO_PAT`** Actions secret — a fine-grained PAT with
`contents:read` on `Chrison-dev/Homelab`, stored in Bitwarden as *"Homelab Schema
Read PAT"*. **Whenever you extract/create a new stack repo, make sure it's in the
secret's visibility scope**, else its `validate` check fails at the download step:

```bash
# zero-maintenance: covers every current + future private stack repo
bw get password "Homelab Schema Read PAT" | gh secret set SCHEMA_RO_PAT --org Chrison-dev --visibility all
# or tight (must re-run with the new repo appended each time):
bw get password "Homelab Schema Read PAT" | gh secret set SCHEMA_RO_PAT --org Chrison-dev \
  --visibility selected --repos Homelab.Stacks.SmartHome,Homelab.Stacks.BuildLab,Homelab.Stacks.<New>
```

The caller passes it through as `schema_token` (underscore — GitHub secret ids
forbid hyphens). Setting org secrets needs org-admin; scope the PAT to `contents:read`.

## Key Commands

### Testing Scripts Locally (Debian test container)

```bash
# Start the test container (mounts src/Proxmox as read-only)
cd .containers/homelab
docker compose up -d debian-test

# Exec into it and run scripts
docker compose exec debian-test bash
./scripts/inventory.sh
pwsh ./scripts/inventory.ps1

# Start NFS test server alongside (for NFS mount testing)
docker compose up -d nfs-test
# NFS server available at 192.168.100.10 inside the test network
```

### Monitoring Stack

```bash
cd stacks/monitoring
# Requires .env file with: RADARR_API_KEY, RADARR_URL, SONARR_API_KEY, SONARR_URL,
#   PROWLARR_API_KEY, PROWLARR_URL, GRAFANA_PORT, GF_SECURITY_ADMIN_USER, GF_SECURITY_ADMIN_PASSWORD
docker compose up -d
# Prometheus: http://localhost:9090
# Grafana:    http://localhost:3000 (or $GRAFANA_PORT)
# SNMP:       http://localhost:9116
# Servarr:    http://localhost:9707
```

> **Note:** The legacy `infra/` directory (Ansible DSM automation + OpenTofu
> Synology spike) was retired — both are superseded by the planned C#-native
> SynoSharp. The monitoring stack moved to `stacks/monitoring/`. See git history.

## Architecture

### Directory Layout

- **`src/Proxmox/`** — Bash and PowerShell scripts deployed directly to Proxmox nodes. Scripts exist in both `.sh` and `.ps1` variants with equivalent functionality.
- **`stacks/monitoring/`** — In-repo monitoring stack: SNMP Exporter → Prometheus → Grafana, plus Servarr Exporter for *arr apps. (Was `infra/docker/monitoring/`.)
- **`.containers/homelab/`** — Debian 13 (Trixie) test container matching the Proxmox OS. Used for local validation of `src/Proxmox/` scripts.
- **`.containers/proxmox/`** — Containerized Proxmox for local dev (requires `/dev/kvm`, Linux only).
- **`.containers/dsm/`** — Virtual DSM container (Synology) for local testing, exposed on port 5000.
- **`docs/`** — Network architecture, device inventory, and script documentation.
- **`.devcontainer/`** — VS Code Dev Container (Ubuntu base) with PowerShell, Ansible, Terraform, and Docker extensions pre-configured.

### Network Architecture

- **Homelab VLAN**: `10.10.0.0/16`
- **Consumer VLAN**: `10.20.0.0/16`
- **IoT VLAN**: `10.40.0.0/16`
- **Network Devices**: `10.0.0.0/16`
- **Legacy** (being deprecated): `192.168.178.0/23`
- Managed via Unifi Cloud Gateway

### Scripting Conventions

- Every script in `src/Proxmox/` has both a `.sh` (Bash) and `.ps1` (PowerShell Core) version — keep them functionally in sync.
- NFS mounts are configured at the **Proxmox host level** (not inside LXC containers) for performance and FS-Cache potential.
- Scripts are designed to be fetched and run remotely (wget/curl from a URL) as documented in `docs/Scripts.md`.
