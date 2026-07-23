# ADR-0007 — Pangolin as the remote-access front door (retire Teleport), on-prem as public ingress for the `*.lab` / `*.arr` wildcard zones

- **Status:** Accepted
- **Date:** 2026-06-15 (proposed) · 2026-06-29 (accepted — Pangolin owns public TLS for two wildcard zones via a home-IP port-forward)
- **Deciders:** Chris
- **Relates to:** [ADR-0005 tunnel topology](ADR-0005-cloudflare-tunnel-topology.md)
  (**supersedes its Teleport-front-door decision**, §4–5), [BL-001 Teleport](../plans/BL-001-teleport.md),
  Teleport exposure [#117](https://github.com/Chrison-Homelab/Homelab/issues/117),
  cloudflared redeploy [#118](https://github.com/Chrison-Homelab/Homelab/issues/118),
  [#136](https://github.com/Chrison-Homelab/Homelab/issues/136) (this),
  [#137](https://github.com/Chrison-Homelab/Homelab/issues/137) (China VPN — co-tenant of the future VPS stack)

## Context

ADR-0005 made **Teleport** the authenticated front door for admin UIs, with cloudflared as
dumb transport and a CF Access break-glass for Proxmox/PDM. Teleport (CT 9904, nuc-01) was
chosen (BL-001) for SSH cert + session recording *and* web-UI SSO; Keycloak was decommissioned.

**In practice Teleport never went live.** CT 9904 was provisioned but the proxy/SSO was never
finished — **no live admin path rides it today**. So ADR-0005's front-door layer was *aspirational,
not operational*: there is no working door to cut over from, and nothing to lose by replacing it.

Reassessing (#136): Teleport is heavy for a single-admin lab, and its App-Access SSO overlaps
with what a tunneled reverse proxy already gives. **[Pangolin](https://github.com/fosrl/pangolin)**
([docs](https://docs.pangolin.net/)) collapses transport + identity into one tool:

- **WireGuard-based.** **Newt** (userspace client, no root) dials **outbound** from the homelab
  to a **Gerbil** WireGuard server; **Traefik** proxies; the **Pangolin** server is the
  dashboard + IdP + access control. Same outbound-only / no-inbound-ports property as cloudflared.
- **Built-in identity.** Platform SSO + OIDC (bring-your-own IdP) + password/PIN + email, with
  **resource-level RBAC** (access to *a resource*, not the whole network). This subsumes the
  CF Access OTP layer.
- **Resource types.** HTTP/HTTPS (browser, clientless) + private TCP/UDP via client, including a
  newer **SSH resource** type.
- **Public ingress** normally comes from a VPS running Gerbil (WireGuard server) with a public IP —
  Newt dials out to it and traffic flows through *your* VPS, not Cloudflare's edge. **But that's only
  needed when Pangolin is the public *entry point*.** Behind an existing tunnel, Pangolin's Traefik
  just proxies local targets and needs no public IP.

Two gaps vs Teleport, **by design**: **no SSH certificate issuance, no SSH session recording.**
Pangolin gates/tunnels SSH but does not replace an SSH CA + audit trail.

**Crucially, Pangolin the software needs no VPS for our use case.** A public VPS only buys making
Pangolin its *own* public ingress (its Gerbil/Newt WireGuard path) — i.e. getting *off* Cloudflare.
We already have public ingress: the cloudflared `core` tunnel. So Pangolin could run on-prem behind it
(cloudflared → Pangolin Traefik), proxying admin UIs as local resources, with no VPS. A VPS is a
*separate, optional* track driven mainly by **#137** (China VPN — CF is blocked in CN, needs a direct
endpoint), **not** a destination this rollout has to reach.

### Finalisation (2026-06-29): the wildcard-subdomain driver

What moved this from Proposed to Accepted is a concrete need the behind-cloudflared shape can't meet
for free: **nested wildcard subdomains**. Cloudflare's free Universal SSL covers only the apex + one
wildcard level (`chrison.dev`, `*.chrison.dev`); deeper names like `*.lab.chrison.dev` get no valid
edge cert without Advanced Certificate Manager (~$10/mo). **Behind cloudflared, Cloudflare terminates
TLS, so that limit applies regardless of Pangolin.** Pangolin only dissolves it when **it** terminates
TLS — its Traefik mints Let's Encrypt wildcard certs (DNS-01, any depth) — which requires Pangolin to
be the **public ingress**, not a backend behind the tunnel.

We accept that trade for two trial zones: **`*.lab.chrison.dev`** (general lab admin UIs) and
**`*.arr.chrison.dev`** (the arr stack admin UIs). To avoid a VPS for the trial, public ingress is a
**home-IP `:443` port-forward** straight to Pangolin's Traefik. This **consciously suspends the
outbound-only / no-inbound-ports invariant for these two zones only** — a deliberate, scoped,
reversible exception, not a repeal. The cloudflared `core` tunnel and its break-glass are untouched; a
VPS (Gerbil) remains the way to *restore* outbound-only later and is the documented graduation path
(still deferred to #137).

The free **Enterprise Edition license key** (personal-use tier) has been obtained — it unlocks the
OIDC/external-IdP + RBAC the SSO goal leans on (the dual-licensing move since 1.18.4 put those behind
the key).

## Decision

**Adopt Pangolin as the remote-access / SSO front door, replacing Teleport's App-Access role.
Run it on-prem in Core. Pangolin is the *public ingress* for two new wildcard zones
(`*.lab.chrison.dev`, `*.arr.chrison.dev`) via a *home-IP `:443` port-forward*, terminating its own
Let's Encrypt wildcard certs (DNS-01) — a scoped, reversible suspension of the outbound-only invariant
for these zones only, with a VPS (Gerbil) as the documented exit. No VPS for now (still deferred to
#137). Everything else stays on its CF stack-tunnel, and the Proxmox/PDM break-glass stays on the
independent HA `core` CF tunnel — never behind Pangolin or the port-forward.**

1. **Pangolin owns the human auth surface; CF tunnels keep transport for everything else.** Admin
   UIs currently fronted by Teleport App Access move behind Pangolin SSO. Machine/public endpoints
   (forgejo webhooks, ERP, Plex clients) and per-stack app ingress stay on their CF stack-tunnels
   — Pangolin's auth buys them nothing.

2. **Break-glass stays on Cloudflare, never behind Pangolin.** Same bootstrap-trap logic as
   ADR-0005 §5: the escape hatch must not depend on the front door you might be repairing. Proxmox
   node UIs + PDM remain on the HA `core` tunnel behind CF Access (the service-token path for
   `proxmox.chrison.dev` is preserved). Pangolin is explicitly **not** in that path.

3. **Pangolin on-prem in Core, public ingress via home-IP `:443` port-forward (add-only, reversible).**
   Pangolin runs as a Core member (LXC in the 2010-block). UniFi forwards WAN `:443` → Pangolin's
   Traefik; Traefik terminates TLS with **Let's Encrypt wildcard certs via the DNS-01 challenge**
   (reusing the existing Cloudflare DNS-edit token), enforces Pangolin auth (badger), and proxies
   admin UIs as *local* resources. `*.lab.chrison.dev` and `*.arr.chrison.dev` are **DNS-only
   (grey-cloud) A records → the home public IP** — proxying them would hand TLS back to Cloudflare and
   re-impose the one-level wildcard limit. Newt/Gerbil WireGuard stays idle (no remote sites). Migrate
   admin hostnames onto Pangolin one at a time, verifying each; decommission Teleport's CT alongside.

4. **A public VPS is the graduation path that *restores* outbound-only — deferred, not this rollout.**
   The home-IP port-forward is the trial's expedient, not the destination: it trades the
   no-inbound-ports invariant for zero new infra. Standing up a VPS running Gerbil (Newt dialing out
   from home) moves the public `:443` off the home IP and back to outbound-only, and is also what
   **#137** needs (a China-reachable endpoint; CF is blocked in CN). When either the trial proves out
   or #137 lands, the *same* Pangolin extends onto a VPS and the two wildcard zones' A records cut from
   the home IP to the VPS — add-only, one at a time. **Until then, no VPS.** Scoped in its own ADR.

5. **Decommission Teleport (CT 9904) — it never went live.** There is no working door to cut over
   from, so nothing retires "last": CT 9904 is dead weight, removable at any time independent of the
   Pangolin rollout. SSH continues via keys (optionally tunneled later as a Pangolin SSH resource).
   Cert-based SSH + session recording were *planned* (BL-001) but never realized — so dropping them
   costs nothing operationally. Keycloak stays decommissioned (BL-001).

### When (if ever) a VPS becomes warranted

Only if one of these becomes true — otherwise never:

- An endpoint must leave Cloudflare's edge — chiefly **#137's CN-reachable endpoint** (CF blocked in
  CN), or a non-HTTP resource cloudflared can't serve.
- You deliberately choose to de-risk the hard Cloudflare dependency (cf. the discover-drift WAF-403
  episode that forced the self-hosted runner).
- Pangolin's WireGuard site-to-site features are wanted beyond cloudflared's reach.

**None of these is required for the admin-UI SSO goal** — that is met on-prem today. For the wildcard
trial the VPS is the *graduation path* (it retires the home-IP inbound exposure and restores
outbound-only), not a prerequisite.

### Target

| Hostname | Path | Auth |
|---|---|---|
| `teleport.chrison.dev` | decommission (never went live) | — |
| `*.lab.chrison.dev` (general lab admin UIs → `grafana.lab`, `ha.lab`, `traefik.lab`, …) | home-IP `:443` → Pangolin Traefik (LE wildcard, DNS-01) | Pangolin SSO/RBAC |
| `*.arr.chrison.dev` (arr stack admin UIs → `radarr.arr`, `sonarr.arr`, `prowlarr.arr`, `bazarr.arr`, …) | home-IP `:443` → Pangolin Traefik (LE wildcard, DNS-01) | Pangolin SSO/RBAC |
| `proxmox.chrison.dev`, `pdm.chrison.dev` | `core` tunnel direct (unchanged) | CF Access (**break-glass — not Pangolin**) |
| forgejo webhooks, ERP, Plex | their CF stack-tunnels (unchanged) | direct / existing |

## Alternatives considered

- **Keep Teleport** — rejected per #136: heavy for one admin and its SSO overlaps a reverse proxy.
  Note it is the only component here doing SSH cert + recording (the accepted loss, below).
- **Pangolin on a VPS from day one** — rejected: spends VPS cost/setup and adds public surface for a
  goal (admin-UI SSO) fully met on-prem behind the existing tunnel. The VPS only earns its keep for
  #137-class off-Cloudflare needs.
- **Stay on raw CF Access OTP, no Pangolin** — viable for break-glass today, but no real IdP, no
  RBAC, no single sign-on across admin UIs. Pangolin behind the same tunnel adds those for one CT.
- **Pangolin Cloud (hosted control plane)** — left open (see Open decisions); self-hosted keeps the
  data plane + IdP in our control, consistent with the repo's self-host bias.
- **Move break-glass behind Pangolin too** — rejected: reintroduces the bootstrap trap ADR-0005 closed.
- **Stay behind cloudflared + buy CF Advanced Certificate Manager** (~$10/mo + Total TLS) for the
  nested wildcards — rejected: pays Cloudflare the exact recurring fee Pangolin's own Let's Encrypt
  avoids, for a single-admin lab. The port-forward trial costs nothing and proves the Pangolin path.
- **VPS (Gerbil) from day one for the wildcards** — deferred, not rejected: it is the *correct* end
  state (restores outbound-only, serves #137), but spends a box + recurring cost before the trial has
  shown the Pangolin/SSO/wildcard model is worth keeping. Graduate to it once it has (decision #4).

## Consequences

- **+** One tool for transport + SSO + RBAC; Teleport CT retired (RAM/vCPU freed); the admin door
  becomes self-hosted *identity*, not just CF Access PINs.
- **+** **Nested wildcards for free** — `*.lab` / `*.arr` served by Pangolin's own Let's Encrypt
  certs, no Cloudflare ACM (~$10/mo) and no per-hostname cert/ingress wiring; a new service is just a
  DNS record + a Pangolin resource.
- **+** Add-only and reversible — break-glass untouched on the `core` tunnel; backing out means
  pulling one port-forward + two wildcard DNS records and Pangolin's public exposure is gone.
- **~** No real loss of SSH session recording / cert-based SSH — planned via Teleport (BL-001) but
  never realized, so there is nothing operational to give up. Revisit a dedicated SSH-audit path
  only if access ever goes multi-user.
- **−** **Suspends the outbound-only / no-inbound-ports invariant for these two zones.** The home IP
  is exposed in public DNS and answers `:443` directly — no Cloudflare edge / WAF / DDoS in front.
  Mitigated by Pangolin auth on every resource, `:443`-only, WAN firewall/geo-limits, and (optionally)
  a DMZ VLAN; retired entirely by graduating to a VPS (decision #4). The deliberate cost of the trial.
- **−** A per-hostname migration list (admin UIs → `*.lab`, arr UIs → `*.arr`) to track during cutover.
- **−** ADR-0005's "Teleport is the door" decision (§4–5) is **superseded**; its per-stack-tunnel +
  HA-replica model still stands.

## Implementation notes — validated by local spike (2026-06-20)

Stood up `fosrl/pangolin:1.18.4` locally (app container only, no Gerbil/Traefik) to validate the
deploy shape and the behind-cloudflared model. Findings:

- **community-scripts ships it.** `ct/pangolin.sh` + `install/pangolin-install.sh` exist (pinned to
  **1.18.4** — schema changes break unattended updates). So the shape is `app: pangolin` like
  cloudflared; **add a `pangolin` entry to `app-catalogue.yaml`** (`script: pangolin`, likely a
  post-create provisioner — see the TLS wrinkle below). It's a **native systemd install** (Node 24;
  `pangolin` + `gerbil` + `traefik` binaries; SQLite OSS edition), *not* Docker.
- **CT defaults:** 2 vCPU / 4096 MB / 10 GB / Debian 13 / unprivileged, **needs TUN** (`var_tun=1`,
  for Gerbil's WireGuard). Tags `proxy`. Fits the Core 2010-block.
- **Ports:** Traefik **80/443** (public ingress + Let's Encrypt), Dashboard Web UI **3002**, Dashboard
  API **3000**, Internal API **3001**; Gerbil WireGuard **51820/udp** + 21820/udp. App serves the
  dashboard over plain **HTTP** (verified 200 on :3002) — no TLS needed internally.
- **Config:** `/opt/pangolin/config/config.yml` (`app.dashboard_url`, `domains`, `server.secret`,
  `gerbil.base_endpoint` — *required even if Gerbil is idle*, `flags`) + `config/traefik/*`. Raw
  TCP/UDP (incl. SSH) resources are gated by `flags.allow_raw_resources: true`. Setup = read a
  one-time **setup token from the logs** → `/auth/initial-setup` → create admin + org.
- **TLS — keep Let's Encrypt, switch to a DNS-01 wildcard.** The stock install configures Traefik to
  *own* public ingress with Let's Encrypt — which is exactly our model now. But the stock
  **`httpChallenge` can't issue wildcards**; `*.lab` / `*.arr` require the **DNS-01 challenge**. The
  provisioner configures a `letsencrypt` certResolver with `dnsChallenge.provider=cloudflare` and the
  existing Cloudflare DNS-edit token (`CF_DNS_API_TOKEN`, from `secrets.env` / Bitwarden), requesting
  `*.lab.chrison.dev` + `*.arr.chrison.dev` (plus the two apexes as SANs if used). Only `:443` need be
  forwarded (DNS-01 needs no inbound `:80`); keep an optional `:80` HTTP→HTTPS redirect if convenient.
  *(The earlier proposed shape — plain HTTP on :80 behind cloudflared, certResolver dropped — is
  superseded by the public-ingress decision.)*
- **DSL / provisioner deltas to implement (handoff to the build session).** `stacks/Core/pangolin.lxc.yaml`
  currently encodes the *superseded* behind-cloudflared model (`edge: cloudflared`, ssl forced off,
  Traefik plain HTTP on :80, a "NO VPS / behind the core tunnel" header). To match this ADR: flip the
  edge mode to a **public-ingress / LE-wildcard mode** (Traefik owns `:443`, DNS-01 wildcard certs via
  `CF_DNS_API_TOKEN`); stop forcing `ssl` off; add the **`zone`** field to `spec.config.resources[]`;
  and correct the header/comments. `PangolinProvisioner` (#145) changes to match.
- **Gerbil/WireGuard is unused** in the on-prem-behind-cloudflared model (no Newt clients) — installed
  but idle. Confirms **no VPS / no Newt** needed for the SSO goal, as decided above.
- **Ingress integration.** Public ingress is the **home-IP `:443` port-forward** (UniFi WAN → Pangolin
  Traefik); Traefik routes by Host header to the backend and the **badger** plugin enforces Pangolin
  auth (redirect-to-login). A new service is a **DNS-only A record under `*.lab` / `*.arr` + a Pangolin
  resource** — no per-service cloudflared ingress rule, no per-hostname cert. The `core` tunnel is no
  longer in this path (it stays for break-glass + existing one-level hostnames only).
- **Port-forward + DNS (IaC).** Add a UniFi WAN `:443` → Pangolin-CT port-forward (via `unifisharp` /
  UniFi MCP, add-only). `*.lab.chrison.dev` + `*.arr.chrison.dev` are **grey-cloud (DNS-only) A records
  → the static home WAN IP** (the same IP `chrison.dev` already resolves to; `quic.nz` rDNS points
  back). Static IP ⇒ **no DDNS** needed.
- **Exposure mitigations (the cost of suspending outbound-only).** The home IP is now in public DNS and
  answers `:443` directly — no Cloudflare edge / WAF / DDoS in front. Mitigate: **every** resource
  under the two zones is a Pangolin resource with auth on (nothing unauthenticated unless deliberately
  public); forward **only `:443`**; tighten the UniFi WAN firewall (consider geo-limits); and consider
  isolating the Pangolin CT in a DMZ VLAN so a compromise can't pivot into Core. Decision #4 (graduate
  to a VPS) retires the inbound exposure entirely.
- **Enterprise Edition key.** The free personal-use **EE license key** (already obtained) must be
  provisioned to unlock OIDC/external-IdP + RBAC (gated since the post-1.18.4 dual-licensing). Store it
  as a secret (`secrets.env` / Bitwarden) and install it on first boot; the dashboard then activates EE.

## Open decisions

1. **Per-hostname map lives in the DSL (renamable IaC).** The hostname → backend map is declared in
   the Pangolin stack DSL (`spec.config.resources[]` in `stacks/Core/pangolin.lxc.yaml`) and
   provisioned via the :3003 integration API (add-only, idempotent by `fullDomain`) — so a DNS name is
   changed by editing the DSL and re-converging, never hand-set in the UI. The DSL must gain a
   **wildcard-zone** dimension per resource (`lab` / `arr`) → `fullDomain = <subdomain>.<zone>.chrison.dev`
   (today it assumes one-level `<subdomain>.chrison.dev`). Zones are set (`*.lab` = general lab admin
   UIs; `*.arr` = arr stack admin UIs); the exact `subdomain.zone` per service is an operational pass
   during cutover. Plex stays direct on the media tunnel (clients can't SSO).
2. ~~SSH session recording~~ — **resolved (2026-06-20):** Teleport never went live, nothing to drop.
3. ~~Pangolin Cloud vs self-hosted~~ — **resolved:** self-hosted control plane (repo self-host bias),
   running the free **EE key** for OIDC/RBAC — not Pangolin Cloud.
4. ~~VPS provider/region~~ — **deferred (not blocking):** no VPS for the trial; it's the graduation
   path (decision #4) to retire the inbound exposure / serve #137. Scoped separately when warranted.
5. ~~DDNS for the home WAN IP~~ — **resolved:** the WAN IP is **static** (`chrison.dev` already
   resolves to it; `quic.nz` rDNS points back). The `*.lab` / `*.arr` grey-cloud A records point at
   the static IP directly — no DDNS updater needed.

## Out of scope

- A standalone VPS stack — only if #137 (or a deliberate leave-Cloudflare decision) ever warrants
  one; its own ADR then. Not part of this rollout.
- #137 China VPN implementation (Hiddify / 3X-UI) — co-tenant of the VPS stack; tracked separately.
- Per-stack VLAN work and SSH-agent rollout details.
