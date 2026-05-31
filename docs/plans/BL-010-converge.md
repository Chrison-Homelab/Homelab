# Plan: BL-010 — Converge: post-create config, dependencies & secrets

**Issue:** [#45](https://github.com/Chrison-dev/Homelab/issues/45) (Project #7 "Homelab Backlog") ·
**Relates to:** [BL-013 community-scripts deploy](BL-013-community-scripts-deploy.md), [BL-009 discover](BL-009-proxmoxsharp-codegen.md), [iac-csharp-native](iac-csharp-native.md), [ADR-0001](../adr/ADR-0001-iac-tooling.md)
**Status:** Planned — 2026-05-31. Declarative contract landed in the schema; the engine (provisioners + `converge` command) is the build.

## Problem

The shape contract + `Deploy-Shape.ps1` stop at **"LXC created + app installed."**
Everything after — wiring services together, app config, secrets — was done **by
hand** when the DevOps stack (BL-015) went live:

| Service | Manual post-create step |
|---|---|
| forgejo-runner | minted a runner **registration token** from Forgejo's CLI, passed `var_forgejo_instance` + token + uuid + labels as raw vars |
| github-runner | minted an **org registration token** from a PAT, ran `config.sh` against the org |
| forgejo | edited `ROOT_URL`/`DOMAIN` to the public hostname |
| cloudflared | created tunnel + DNS via API, installed the **tunnel token** into the CT |

None of that is reproducible from the shapes. This plan makes it IaC.

## Decisions (2026-05-31)

- **Secrets backend = `secrets.env`** (the existing gitignored file + GitHub Actions secrets). Shapes carry *references* (`env:` names) or *derivation verbs* — never inline values.
- **Home = the ProxmoxSharp converge engine** (BL-010). The community-scripts renderer (BL-013) stays bring-up-only; converge owns config + lifecycle, per ADR-0001.

## Declarative contract (landed in `shape.schema.json`)

Three converge-only fields on `kind: LXC`:

- **`dependsOn: [name]`** — converge orders shapes topologically; a dependency's resolved attributes (URL/IP from discovery) are available to this shape.
- **`config: { … }`** — app-specific post-create settings a provisioner applies.
- **`secrets: [{ name, valueFrom }]`** — resolved at provision time. `valueFrom` is **exactly one** of:
  - `env: NAME` — **pre-existing**, from `secrets.env` (e.g. `GH_RUNNER_PAT`, `CF_API_TOKEN`).
  - `service: { ref, action, with? }` — **derived** by invoking an action on another shape (e.g. Forgejo `generate-runner-token`).
  - `provider: { name, action, with?, auth }` — **derived** from an external API (GitHub/Cloudflare); `auth` is itself a `valueFrom` (typically `env:`).

The crux: secrets come in **two kinds** — *pre-existing* (`env:`) and **derived** (the registration/tunnel tokens that only exist once you call the forge/provider). The derived kind is what made everything manual.

## The four cases, declaratively

```yaml
# forgejo            → config: { rootUrl: https://forgejo.chrison.dev }
# forgejo-runner     → dependsOn: [forgejo]; config: { runnerLabels: [homelab] }
#                      secrets: [{ name: runnerToken, valueFrom: { service: { ref: forgejo, action: generate-runner-token } } }]
# github-runner      → config: { githubOrg: Chrison-dev }
#                      secrets: [{ name: registrationToken, valueFrom: { provider: { name: github, action: org-runner-token, with: { org: Chrison-dev }, auth: { env: GH_RUNNER_PAT } } } }]
# cloudflared        → dependsOn: [forgejo]; config: { tunnel: Homelab.Stacks.DevOps, ingress: [...] }
#                      secrets: [{ name: tunnelToken, valueFrom: { provider: { name: cloudflare, action: tunnel-token, auth: { env: CF_API_TOKEN } } } }]
```

(Authored into the `stacks/DevOps` shapes as the reference implementation.)

## Engine design (the BL-010 build — not yet implemented)

A `proxmoxsharp converge <stack>` command:

1. **Load** all shapes in the stack (+ stack defaults merge, as the renderer does).
2. **Order** by `dependsOn` (topological; cycle = error).
3. Per shape, **ensure the CT exists** — delegate create to the community-scripts path (idempotent: skip if present).
4. **Resolve secrets** — `env:` from `secrets.env`; `service:`/`provider:` by invoking the matching provisioner/provider at run time. Resolved values live only in memory.
5. **Run the app provisioner** — an app-keyed C# class encoding the manual steps:
   - `ForgejoProvisioner` — set `ROOT_URL`/`DOMAIN`; expose `generate-runner-token`.
   - `ForgejoRunnerProvisioner` — register against the `dependsOn` Forgejo with the derived token + labels.
   - `GithubRunnerProvisioner` — mint org token via the GitHub provider, run `config.sh`.
   - `CloudflaredProvisioner` — ensure tunnel + DNS (add-only, per CLAUDE.md), install the tunnel token.
   - Providers: `GithubProvider`, `CloudflareProvider` (auth via `env:` refs).

Properties: **idempotent**, **secret-free in git** (only `env:` names + `action:` verbs committed), and honours the **add-only external-accounts guardrail** (CLAUDE.md) for GitHub/Cloudflare actions.

## Deliverables

1. **Schema** — `dependsOn`, `config`, `secrets` + the `secret`/`secretSource` `$defs`. ✅ Landed + validated (positive/negative).
2. **Reference shapes** — `stacks/DevOps/*` annotated with the real post-create wiring. ✅
3. **Plan** — this doc.
4. **Engine scaffold** — `Infrastructure/engine` now has: shape models + YAML loader (stack-defaults merge), `secrets.env` reader, topological `dependsOn` ordering, a `SecretResolver` (env-presence + derived descriptors), an app-keyed `ProvisionerRegistry` (Forgejo/ForgejoRunner/GithubRunner/Cloudflared), and a **`converge <stack>` dry-run** command that prints the ordered post-create plan. ✅
5. **Apply framework + first provisioner** — `converge --apply` with `NodeExec` (SSH → `pct exec`), guards (CT must exist; required env secrets present), and an **idempotent `ForgejoProvisioner.ApplyAsync`** (reads `ROOT_URL`, rewrites + restarts only if changed). The runner/cloudflared provisioners report **Skipped** until their idempotency checks land (re-registering a live runner or re-creating the tunnel would churn the working stack). ✅
6. **Full apply (idempotent)** — `SecretDeriver` (env / Forgejo CLI `generate-runner-token` / GitHub `org-runner-token`) + REST clients (`GithubApi`, `CloudflareApi`), and `ApplyAsync` for all three remaining provisioners, each **idempotency-first**: ForgejoRunner (skip if daemon active), GithubRunner (skip if runner online in org), Cloudflared (skip if tunnel+DNS exist & connector active; ADD-ONLY create otherwise). Built + compiles. **Mutation branches are not yet live-exercised** — they only fire when a resource is absent, so a run against the already-provisioned stack no-ops. ✅
7. **Remaining** (next): ProxmoxSharp CT create/lifecycle (so converge can build a stack from nothing), resolve logical ingress hosts → CT IPs for live cloudflared create, and a first authorized live `--apply` to exercise a mutation path safely. ⏳

## Out of scope

- The engine implementation itself (this round is contract + plan only).
- Non-`secrets.env` backends (Bitwarden/SOPS) — revisit if the env file stops scaling.
- Destroy/update lifecycle beyond converge (separate BL-010 phase).
