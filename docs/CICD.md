# CI/CD

GitOps for the homelab hub. **[Fallout](https://github.com/ChrisonSimtian/Fallout)**
(the C#/.NET build system) drives CI; **GitHub Environments** and **GitHub
Releases** track the state of the lab.

## Fallout build

The build is a C# project (`_build/`) scaffolded by `fallout :setup`. CI workflows
are declared as `[GitHubActions(...)]` attributes on the `Build` class and
generated into `.github/workflows/`. The pinned tool lives in
`.config/dotnet-tools.json`; bootstrap via `build.ps1` / `build.sh`.

Run locally:

```powershell
dotnet tool restore
dotnet fallout            # default target
```

**Planned first target — `ValidateShapes`:** validate every `/Infrastructure`
shape YAML against `Infrastructure/schema/shape.schema.json`, so bad shapes fail
CI. (Pending the `fallout :setup` scaffold.)

## GitHub Environments

Used to track *where things are deployed* and gate changes. Created in repo
settings (per the per-node + umbrella decision):

| Environment | Represents |
| --- | --- |
| `homelab` | Umbrella — whole-lab deployments / state snapshots |
| `hpe-01` | Proxmox node hpe-01 |
| `nuc-01` | Proxmox node nuc-01 |
| `desktop-01` | Proxmox node desktop-01 |

Each deployment to an environment records a GitHub Deployment → a per-target
history and current state. Protection rules (required reviewers, wait timers) and
environment-scoped secrets can be added per environment as provisioning comes
online.

> Environments are GitHub repo settings (not in-repo files). They were created via
> the API; managing them declaratively (e.g. a settings app / Terraform GitHub
> provider) is a possible future step to keep with the IaC ethos.

## GitHub Releases

A Release is an **on-demand, versioned snapshot of the homelab's declared state** —
the committed inventory (`docs/`, `Infrastructure/`) plus the pinned
`Homelab.Stacks.*` submodule versions. Cut manually via the
[`release-state-snapshot`](../.github/workflows/release.yml) workflow
(Actions → run workflow → enter a version + notes), recorded against the `homelab`
environment.

Each release attaches `homelab-state-<version>.tar.gz` and a `MANIFEST.md`
(commit + `git submodule status`), giving a timeline of known-good states.

> Live cluster discovery is **not** captured in CI (GitHub-hosted runners can't
> reach the homelab network). Capturing live state would run on the self-hosted
> runner (LXC 2005) — a later enhancement.

## Private submodule auth (BL-007)

CI checks out the private `Homelab.Stacks.*` submodules using a **fine-grained
PAT** (read-only, scoped to those repos) stored as the Actions secret
**`SUBMODULES_PAT`**. Workflows pass it to `actions/checkout` via `token:`.

> Rotate the PAT periodically. A GitHub App (short-lived tokens, no rotation) is
> the longer-term upgrade if this grows.

## Setup checklist

- [x] Create GitHub Environments (`homelab`, `hpe-01`, `nuc-01`, `desktop-01`)
- [x] Add the on-demand release workflow
- [ ] Run `fallout :setup` to scaffold the `_build` project
- [ ] Add the `ValidateShapes` target + `[GitHubActions]` CI workflow
- [ ] Create the fine-grained PAT and store it as `SUBMODULES_PAT`
