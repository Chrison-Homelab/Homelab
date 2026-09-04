# ADR-0012 — Homepage as the homelab dashboard, rendered from the shapes (never hand-kept)

- **Status:** Accepted
- **Date:** 2026-09-05
- **Deciders:** Chris
- **Relates to:** [#47](https://github.com/Chrison-Homelab/Homelab/issues/47) (generate a service
  directory from the shapes — this implements it), [ADR-0009](ADR-0009-podman-quadlet-migration.md)
  (how the container lands on CT 4001), [ADR-0007](ADR-0007-pangolin-remote-access.md) (the Pangolin
  resource declarations this reads), [ADR-0008](ADR-0008-stack-extraction-meta-repo.md) (stack
  submodules — the render reads across them), [#252](https://github.com/Chrison-Homelab/Homelab/issues/252)
  (Docusaurus docs site — considered as the host, see alternatives).

## Context

Nothing answered "what do we run and where is it". Shapes carry node, CT id and app; Pangolin
resources carry the public URL and whether SSO gates it; DHCP reservations carry the internal name.
The answer was reassembled by hand each time and misremembered (#47: CT 4000 was recalled as the
monitoring host long after it became 4001).

The tempting fix — install a dashboard and click services into it — creates a **second, hand-kept
list** that drifts the moment a CT is added and nobody updates the dashboard. #47 recorded that
rejection on 2026-08-22. The requirement here is stronger than "have a dashboard": **when a new app
is deployed, the dashboard updates without anyone editing the dashboard.**

That reframes the evaluation. The question is not which dashboard is nicest to click together; it is
which dashboard can be **fed by a generator** from declarations the repo already maintains.

| Candidate | Config | Generator-friendly | Live widgets for what we run | Reload |
|---|---|---|---|---|
| **Homepage** (gethomepage) | YAML files | Yes — one `services.yaml` | Proxmox, UniFi, all four arr apps, qBittorrent, Plex, Seerr, Home Assistant, Authentik, Grafana, Prometheus, Traefik, Cloudflare tunnels, Audiobookshelf, ESPHome, Synology, Gitea, Romm, Pulse, Beszel, custom API | container restart |
| Glance | YAML, `$include` | Yes; per-stack include fragments suit the meta-repo | none of ours natively — HTTP monitor, Docker, custom API only | hot reload |
| Dashy | YAML, multi-file | Yes | status checks only | rebuild |
| Homarr | database, UI-first | only via its REST API | good arr/Docker integrations | n/a |
| Static page on Docusaurus (#252) | generated Markdown | Yes | none | site build |

## Decision

**Homepage, deployed as one more quadlet on the Monitoring podman host (CT 4001, `:3010`), with its
`services.yaml` rendered from the shapes by the engine and pushed on every merge to `main`.** LAN-only;
no Pangolin resource, no auth in front of it — it links to things that carry their own gates.

1. **Declaration lives on the shape.** A guest that offers a human a UI declares it under
   `metadata.services[]` — name, internal `url`, optional `group`/`icon`/`description`, and an optional
   Homepage `widget`. It is metadata because it describes the guest; converge never applies it.
   ```yaml
   metadata:
     name: sonarr
     services:
       - name: Sonarr
         url: http://sonarr.homelab.chrison.internal:8989
         widget: { type: sonarr, keyFrom: SONARR_API_KEY }
   ```
2. **The public URL and its gate are derived, never declared twice.** The renderer matches each service
   by name (case- and punctuation-insensitive) against the Pangolin resources and cloudflared ingress
   already declared in the Core and Media stacks, and shows `host (Pangolin SSO)` /
   `host (Cloudflare Access)` in the description. The dashboard therefore says what is *actually*
   exposed, from the same declaration converge applies. `public:` exists only as an override.
3. **Gaps are shown, not hidden.** A Pangolin resource or tunnel hostname that no shape declares is still
   rendered — in a last group named for the omission, linking to its public URL — and
   `homelab-infra dashboard --check` reports it. A publicly exposed UI missing from the dashboard is the
   exact drift this ADR exists to prevent, so it is made visible rather than silently omitted.
4. **Secrets by name, resolved on the host.** A widget says `keyFrom: SONARR_API_KEY`; the render emits
   `{{HOMEPAGE_VAR_SONARR_API_KEY}}`; the `homepage.container` quadlet maps the podman secret to that
   variable. Homepage proxies every widget call server-side, so keys never reach the browser. `--check`
   fails if a widget names a secret the quadlet does not export.
5. **Delivery is a one-file push, not a converge.** `homelab-infra dashboard --deploy` writes the rendered
   file to `<assetsTarget>/homepage/services.yaml` on the shape carrying `config.dashboard`, and restarts
   only `homepage.service`, only when the sha changed. The `dashboard` workflow runs it on every push to
   `main` touching `stacks/**` and on the scheduled submodule bump, so a new app declared in any stack
   repo reaches the dashboard without a person touching it. Converging the Monitoring host would restart
   every monitoring unit; declaring an app elsewhere must not do that.
6. **The repo enforces the rule.** `CLAUDE.md` tells every author and agent that a shape with a UI
   carries `metadata.services`; the schema validates the block; `--check` names anything exposed but
   undeclared. The instruction is not "remember to update the dashboard" — there is nothing to update.

## Alternatives considered

- **Glance** — the honest runner-up: hot reload, and `$include` fragments per stack repo would fit
  ADR-0008 cleanly. Rejected because it has no integrations for anything we run; a dashboard that shows
  queue depth, node state and login counts earns its place, a list of links does not.
- **Homarr** — good integrations, but the board lives in a database edited through the UI. Automating it
  means driving its REST API to mirror the shapes: a sync problem, not a render. Against #47 by design.
- **Dashy** — YAML, but rebuilds on config change and has status checks only.
- **A generated static page on the Docusaurus site (#252)** — satisfies #47 literally and stays an
  option for the *docs* view (node, CT id, mounts, ADR links). It is a directory, not a dashboard: no
  health, no widgets. Homepage's `siteMonitor` gives every tile a live status for free.
- **Render inside converge** — considered and rejected (decision 5). It also would have made the
  Monitoring host's managed marker depend on every other stack's content, so any shape edit anywhere
  re-deployed the monitoring fleet.

## Consequences

- **+** One page answers "what runs where, how do I reach it, and is it up", and it cannot go stale:
  a new shape with `metadata.services` is on the dashboard after the next merge.
- **+** Public exposure is *audited* as a side effect — the last group and `--check` list anything the
  edge serves that no shape owns.
- **+** Widgets for the arr fleet, Pulse, Grafana, Prometheus, Home Assistant and UniFi come from
  secrets already on CT 4001.
- **~** Stack repos (Media, DevOps, SmartHome) must add `metadata.services` to their shapes, and can only
  do so after this schema version is published (`publish-schema.yml` on merge) — until then their UIs
  appear in the undeclared group with public links only. Tracked as follow-ups on #47.
- **~** `homepage.container`'s `Secret=` lines and the shapes' `*From` fields must agree; `--check`
  catches a mismatch, but adding a new widget credential is a two-file change.
- **−** No Proxmox widget yet: the only Proxmox token in secrets is root-scoped and does not belong on a
  dashboard container. Needs a PVEAuditor token first. Pulse's widget covers nodes/VMs/LXCs meanwhile.
- **−** The dashboard host is the monitoring host: when CT 4001 is down, so is the page that would tell
  you what else is down. Accepted — it is LAN-only and the break-glass is Proxmox itself.

## Notes for whoever touches this next

- Render locally with `./build.sh PreviewDashboard` (stdout + check, no mutation); `./build.sh Dashboard`
  pushes. The engine verb is `homelab-infra dashboard <stacks-dir> [--out f] [--check] [--deploy]`.
- The render loads shapes with **lenient `${VAR}` expansion** (`ShapeLoader.LoadStack(..., lenientVars: true)`),
  because it needs metadata only and runs where `secrets.env` may not exist. Converge never does this.
- Widget URLs on CT 4001 use **container names** (`http://pulse:7655`, `http://grafana:3000`) because
  Homepage sits on `monitoring.network` with them; everything else uses the internal DNS name.
- Homepage's `HOMEPAGE_ALLOWED_HOSTS` is a host-header allowlist and is **required**; a new way of
  reaching the dashboard (another name or port) must be added there or every page is a 400.
