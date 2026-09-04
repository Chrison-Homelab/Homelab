# Plan: Secrets from Bitwarden Secrets Manager (single canonical store)

**Status:** Hub built + validated — 2026-07-17. `secrets.env` now generated from
`secrets.env.template` + SM on this Mac; `.sh`/`.ps1` proven byte-identical.
Repo-wide stack rollout still pending. **Graduates to ADR-0008** once approved —
supersedes the "Secrets backend = `secrets.env`" decision in
[BL-010](BL-010-converge.md).
**Relates to:** [BL-010 converge](BL-010-converge.md), [ADR-0001 IaC tooling](../adr/ADR-0001-iac-tooling.md),
[ADR-0007 Pangolin](../adr/ADR-0007-pangolin-remote-access.md)

## Problem

`secrets.env` is the one canonical secrets file for every CLI + the converge hub.
It is hand-maintained and gitignored — which breaks down across machines:

- **Schema drift.** `secrets.env.example` (committed) is already ~9 keys behind the
  live file (missing `HOMEASSISTANT_TOKEN`, `CF_ACCESS_PROXMOX_CLIENT_ID/SECRET`,
  `CF_TUNNEL_MEDIA_TOKEN`, `ABS_USER/PASSWORD`, `HARDCOVER_API_KEY`,
  `PANGOLIN_LICENSE_KEY`). Copying from the example gives you an incomplete file.
- **Manual fill.** On a second machine (the Windows work laptop) the file is either
  absent or half-populated, with no signal about *what's_missing*.
- **No single source of truth.** Values live partly in the vault (`mqtt · …`,
  `arr · …` items), partly only in the Mac's `secrets.env`.

The live file has ~40 keys: ~15 non-secret config (base URLs, `*_VERIFY_TLS`,
`GH_ORG`, `ARR_USER`, `ORCH_*` tunables, OTEL endpoint) and ~25 actual secrets.

## Decision

**Bitwarden Secrets Manager (SM) becomes the single canonical store for every
homelab secret**, read via the `bws` CLI with a per-machine machine-account token.
`secrets.env` is no longer hand-edited — it is **generated** from a committed
template overlaid with SM values.

Why SM over the password vault (`bw`):

- **Non-interactive.** A machine-account **access token** authenticates with no
  master-password prompt — fixes the cross-laptop friction directly.
- **Purpose-built** for machine/env secrets; UUID-addressed, grouped into projects.
- **Grouping via projects** gives the "by-domain" organization without duplicating
  anything across two Bitwarden stores.

Trade-off accepted: SM cannot read existing password-vault items, so the ~25
secrets **migrate into SM**. During transition, secrets that stacks still fetch via
`bw` (vault) at deploy time live in **both** stores until each stack cuts over
(see Repo-wide rollout). This is a known transient, tracked below.

## Design

### 1. Split the file

```
secrets.env.template   ← committed. Replaces secrets.env.example.
                          Non-secret lines = literal values (pass through).
                          Secret lines = empty; key name == SM secret key.
scripts/secrets-sync.sh
scripts/secrets-sync.ps1  ← .sh + .ps1 parity, per repo convention.
                            Reads template, overlays SM values, writes secrets.env.
```

The template is the **single canonical schema** — drift dies because there is one
committed file describing every key, and the generator produces the real file.

Template sketch:

```bash
# ── non-secret: literal, passed through verbatim ──
PROXMOX_BASE_URL=https://hpe-01.homelab.chrison.internal:8006/api2/json
PROXMOX_VERIFY_TLS=false
GH_ORG=Chrison-Homelab
ARR_USER=csimon

# ── secret: filled from Secrets Manager (key == env var name) ──
PROXMOX_TOKEN_SECRET=
CF_API_TOKEN=
SONARR_PASSWORD=
```

No `# bw:` annotations needed: **each SM secret's key is exactly the env var name**,
so the generator overlays by name.

### 2. SM project layout (the "hybrid" grouping)

One machine account, scoped to these projects:

| Project        | Secrets (keys)                                                        |
|----------------|-----------------------------------------------------------------------|
| `homelab-core` | `PROXMOX_TOKEN_*`, `SYNOLOGY_*`, `UNIFI_API_KEY`                       |
| `cloudflare`   | `CF_API_TOKEN`, `CF_ACCOUNT_ID`, `CF_ACCESS_PROXMOX_*`, `CF_TUNNEL_MEDIA_TOKEN` |
| `github`       | `GITHUB_PACKAGES_PAT`, `GH_RUNNER_PAT`                                 |
| `pangolin`     | `PANGOLIN_API_KEY`, `PANGOLIN_LICENSE_KEY`                            |
| `media`        | `QBIT_PASSWORD`, `SONARR/RADARR/PROWLARR/BAZARR_PASSWORD`, `BAZARR_OPENSUBTITLES_*`, `ABS_*`, `HARDCOVER_API_KEY` |
| `smarthome`    | `HOMEASSISTANT_TOKEN` (+ existing `mqtt · …` values migrated in)       |

(Exact mapping finalized during the unlock+audit step.)

### 3. Generator behavior (identical in .sh / .ps1)

1. Require `BWS_ACCESS_TOKEN` (env, or read from OS keychain). Fail clearly if absent.
2. `bws secret list <project…>` → build `key → value` map.
3. For each template line: non-secret → pass through; secret key → fill from map.
4. **Completeness check** — any template secret key SM did not return is reported
   loudly. *This is the "half-filled" alarm, surfaced instead of silent.*
5. Write `secrets.env` mode `600`; never echo values.
6. Idempotent: re-running with unchanged SM state yields a byte-identical file.

### 4. Per-machine bootstrap (the one manual secret)

The `BWS_ACCESS_TOKEN` is the single credential placed by hand per machine:

- **Mac:** stored in Keychain; `secrets-sync.sh` reads it via `security find-generic-password`.
- **Windows:** stored via DPAPI / Credential Manager; `secrets-sync.ps1` reads it.
- Fallback: `BWS_ACCESS_TOKEN` env var if already exported.

## Execution — hub

1. **Unlock + audit.** Enumerate the 25 secrets against the vault; list what already
   exists vs. what's only in the Mac's `secrets.env`. (Vault was locked at drafting.)
2. **Enable SM + create projects + machine account.** Issue the access token
   (UI-only steps in the web vault — done by hand).
3. **Migrate** the 25 secrets into SM (keys = env var names), grouped per §2.
4. **Install `bws`** (Mac + Windows).
5. **Build** `secrets.env.template` + `scripts/secrets-sync.{sh,ps1}`; retire
   `secrets.env.example`; update `README.md` + `CLAUDE.md` (the "source
   `secrets.env`" instructions gain a "regenerate with `secrets-sync`" step).
6. **Verify.** Regenerate `secrets.env` on the Mac; diff vs. current → expect
   identical. Then bootstrap the Windows laptop end-to-end from a clean checkout.

## Execution — repo-wide rollout (single canonical store)

The `Homelab.Stacks.*` repos fetch secrets via `bw` (vault) in their
`bin/load-secrets.ps1` at deploy time. To make SM the *single* store:

1. Each stack's `load-secrets.ps1` switches from `bw get` to `bws secret get`
   (or a shared helper), scoped to its project's machine account.
2. Retire the duplicated vault items once every consumer of a secret reads it
   from SM.
3. Track cutover per stack (mirror the ADR-0005 tunnel-migration status table).

Until a stack cuts over, its secrets exist in both vault and SM — **transient dual
store**, tracked so it doesn't become permanent drift.

## Audit — vault ⇄ 21 secret keys (2026-07-17)

Vault unlocked + synced (797 items total). Mapping of each secret env key to its
current vault home. Non-secret keys (~15: base URLs, `*_VERIFY_TLS`,
`PROXMOX_TOKEN_ID`, `GH_ORG`, `*_USER`, `ORCH_*`, OTEL) stay literal in the template.

**Clean single match (10) — map straight into SM:**

| Env key | Vault item |
|---|---|
| `PROXMOX_TOKEN_SECRET` | Proxmox Homelab Token (`root@pam!homelab`) |
| `SYNOLOGY_PASSWORD` | Synology (`csimon`) |
| `UNIFI_API_KEY` | Unifi Container: Homelab API Key |
| `CF_API_TOKEN` | Cloudflare API Token (`homelab-iac`) |
| `GITHUB_PACKAGES_PAT` | Read:Packages (`Fallout-Build`) |
| `SONARR_PASSWORD` | Sonarr (Homelab Media CT 5101) |
| `RADARR_PASSWORD` | Radarr (Homelab Media CT 5102) |
| `PROWLARR_PASSWORD` | Prowlarr (Homelab Media CT 5100) |
| `BAZARR_PASSWORD` | Bazarr (Homelab Media CT 5103) |
| `HARDCOVER_API_KEY` | hardcover.app (`API Key` field) |

**Resolved candidates (was ambiguous, now canonical):**

| Env key | Canonical vault item | How resolved |
|---|---|---|
| `QBIT_PASSWORD` | qBittorrent WebUI — Media stack (CT 5104) | newer of two (both `admin`) |
| `ABS_PASSWORD` | audiobookshelf (Homelab Media CT 5112) (`root`) | `ABS_USER=root` match |
| `BAZARR_OPENSUBTITLES_PASSWORD` | opensubtitles.com (`Chrison`) | `..._USER=Chrison` + exact domain |
| `PANGOLIN_API_KEY` | Pangolin EE - API Key (`Homelab`) | live EE on CT 2013 (user pick) |
| `HOMEASSISTANT_TOKEN` | Home Assistant Token (`Token` field) | purpose-built token (user pick) |

**Capture from local `secrets.env` (6) — no clean vault home, only on the Mac:**

`CF_ACCOUNT_ID`, `CF_ACCESS_PROXMOX_CLIENT_ID`, `CF_ACCESS_PROXMOX_CLIENT_SECRET`,
`CF_TUNNEL_MEDIA_TOKEN`, `PANGOLIN_LICENSE_KEY`, `GH_RUNNER_PAT`.

`GH_RUNNER_PAT` is only referenced by the BL-010 converge plan (org-runner-token
derivation), not live code — captured now so the key exists when that lands.
These 6 are the "half-filled on the Windows laptop" root cause.

**Net: 21 secret keys → 15 migrate from existing vault items, 6 captured from the
Mac's local `secrets.env`. All 21 land in the SM `homelab` project.**

> SmartHome `mqtt · homeassistant` / `mqtt · leapmotor` items exist in the vault
> (stack-level, not hub keys) — relevant to the repo-wide rollout, not this file.

## Bootstrap done (2026-07-17, this Mac)

- **`bws` v2.1.0** installed to `~/.local/bin/bws` (official `bitwarden/sdk-sm`
  release, `aarch64-apple-darwin`; not on Homebrew). Windows: one-shot
  `scripts/secrets-bootstrap.ps1` (installs bws, pins EU, stores token via DPAPI,
  runs secrets-sync).
- **Region matters:** vault is on **EU** (`vault.bitwarden.eu`); `bws` defaults to
  US → `invalid_client`. Pinned via `bws config server-base https://vault.bitwarden.eu`
  (`~/.config/bws/config`). The generator must set this (or `BWS_SERVER_URL`).
- **Access token** in macOS Keychain — service `homelab-bws-access-token`,
  account `bws` (read: `security find-generic-password -a bws -s homelab-bws-access-token -w`).
  Source of truth: vault item `BW Secrets Manager - Github`.
- **SM `Homelab` project:** id `ceb88092-7a26-4882-9e7b-b48a000a8f9a`,
  org `fd583ce1-2523-4e5d-b169-b32a00ae734d`. Currently **0 secrets**.

## Migration mechanism (decided)

To avoid ~15 vault secret values transiting the agent's context/argv, migration is
a **one-shot script run in a real terminal**: unlock `bw` once (`BW_SESSION`), read
each mapped vault item field + the 6 local `secrets.env` values, `bws secret create`
all 21 into the Homelab project (keys = env var names). Aligns with
[[full-iac-no-manual-steps]] — reproducible, no hand-pushing.

## Built + validated (2026-07-17)

- `scripts/secrets-migrate.sh` — seeded SM from vault (15) + local `secrets.env`
  (6). All 21 created, vault untouched (read-only).
- `secrets.env.template` — committed schema (replaces retired `secrets.env.example`);
  built by blanking the 21 secret keys. Also removed a **commented-out CF token**
  (`#cfut_…`) that had been sitting in `secrets.env`.
- `scripts/secrets-sync.{sh,ps1}` — regenerate `secrets.env` from SM. Proven
  **byte-identical** between the two, and every one of the 38 keys sources to the
  same value as the pre-migration `secrets.env`.
- `CLAUDE.md` + PowerOrchestrator `deploy.sh` updated to point at template/sync.

### Finding + reconciliation: 6 by-name audit mismatches (resolved)

Name-based mapping picked the WRONG vault item for 6 keys (item value ≠ live
`secrets.env` value). Caught by the post-migration hash compare; SM was set to the
**live `secrets.env`** value (authoritative). Then each live value was hash-matched
against *every* vault item to find its true home:

**Correct item existed — I'd just guessed the wrong name** (SM already right; the
migrate-script mapping is repointed to these):

| Key | Correct vault item | (my wrong guess) |
|---|---|---|
| `SYNOLOGY_PASSWORD` | **DSM1813: Homelab** (user `homelab`) | `Synology` (csimon) |
| `UNIFI_API_KEY` | **Homelab MCP API Key** | `Unifi Container: Homelab API Key` |
| `HOMEASSISTANT_TOKEN` | **Home Assistant: Claude Token** | `Home Assistant Token` |

**Not in the vault at all** — the live value lived ONLY in `secrets.env` (genuine
drift), now also in SM. Migrate script sources these as `local`:

- `PROXMOX_TOKEN_SECRET` — the `root@pam!claude-mcp` token secret (vault only has the
  *different* `root@pam!homelab` token).
- `GITHUB_PACKAGES_PAT` — 40-char classic `ghp_` (vault's `Read:Packages` is a
  different 84-char token).
- `PANGOLIN_API_KEY` — no matching vault item.

  → **Open decision:** mirror these 3 into the password vault too, or accept SM as
  their sole Bitwarden home now that SM is canonical.

## Open items / to confirm

- [ ] SM free/current tier limits (machine accounts, project count) cover §2.
- [ ] Whether `mqtt · …` and `arr · …` values are copied into SM or regenerated.
- [ ] Whether the converge hub reads `secrets.env` (regenerated) or calls `bws`
      directly — leaning "regenerate `secrets.env`" to keep one code path.
- [ ] Access-token rotation story (machine-account token lifetime + re-issue flow).
