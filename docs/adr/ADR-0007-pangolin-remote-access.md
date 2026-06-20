# ADR-0007 — Pangolin as the remote-access front door (retire Teleport), on-prem behind the core tunnel

- **Status:** Proposed
- **Date:** 2026-06-15
- **Deciders:** Chris
- **Relates to:** [ADR-0005 tunnel topology](ADR-0005-cloudflare-tunnel-topology.md)
  (**supersedes its Teleport-front-door decision**, §4–5), [BL-001 Teleport](../plans/BL-001-teleport.md),
  Teleport exposure [#117](https://github.com/Chrison-dev/Homelab/issues/117),
  cloudflared redeploy [#118](https://github.com/Chrison-dev/Homelab/issues/118),
  [#136](https://github.com/Chrison-dev/Homelab/issues/136) (this),
  [#137](https://github.com/Chrison-dev/Homelab/issues/137) (China VPN — co-tenant of the future VPS stack)

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
We already have public ingress: the cloudflared `core` tunnel. So Pangolin runs **on-prem behind it**
(cloudflared → Pangolin Traefik), proxying admin UIs as local resources, with **no VPS**. A VPS is a
*separate, optional* track driven mainly by **#137** (China VPN — CF is blocked in CN, needs a direct
endpoint), **not** a destination this rollout has to reach.

## Decision

**Adopt Pangolin as the remote-access / SSO front door, replacing Teleport's App-Access role.
Run it on-prem in Core behind the existing HA `core` CF tunnel — no VPS. A public VPS is a separate,
optional track (driven mainly by #137), not part of this rollout. Keep the Proxmox/PDM break-glass on
the independent `core` CF tunnel.**

1. **Pangolin owns the human auth surface; CF tunnels keep transport for everything else.** Admin
   UIs currently fronted by Teleport App Access move behind Pangolin SSO. Machine/public endpoints
   (forgejo webhooks, ERP, Plex clients) and per-stack app ingress stay on their CF stack-tunnels
   — Pangolin's auth buys them nothing.

2. **Break-glass stays on Cloudflare, never behind Pangolin.** Same bootstrap-trap logic as
   ADR-0005 §5: the escape hatch must not depend on the front door you might be repairing. Proxmox
   node UIs + PDM remain on the HA `core` tunnel behind CF Access (the service-token path for
   `proxmox.chrison.dev` is preserved). Pangolin is explicitly **not** in that path.

3. **Pangolin on-prem in Core, behind the `core` tunnel (add-only) — the end state.** Pangolin runs
   as a Core member (LXC/VM in the 2010-block). The existing HA `core` cloudflared tunnel fronts it
   (cloudflared → Pangolin Traefik, `noTLSVerify` to a local target — the same ingress shape the old
   Teleport entry used); Pangolin proxies admin UIs as *local* resources, so its Newt/Gerbil
   WireGuard data path is unused and there is **no VPS, no new public surface**, fully reversible.
   This is the destination for homelab remote access, not an interim. Migrate admin hostnames onto
   Pangolin one at a time, verifying each; decommission Teleport's CT alongside.

4. **A public VPS is a separate, optional track — not where this rollout heads.** It is only worth
   standing up to make Pangolin (or another endpoint) its *own* public ingress off Cloudflare — and
   the concrete driver is **#137** (a China-reachable endpoint; CF is blocked in CN). If that lands,
   the same Pangolin extends onto a VPS (Gerbil there, Newt dialing out from home) and hostnames can
   migrate off the CF tunnel one at a time, add-only. **Until #137 (or a deliberate leave-Cloudflare
   decision), there is no VPS.** Scoped in its own ADR if it happens.

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

**None of these is required for the admin-UI SSO goal** — that is fully met on-prem behind the `core`
tunnel.

### Target

| Hostname | Path | Auth |
|---|---|---|
| `teleport.chrison.dev` | decommission (never went live) | — |
| admin UIs (Grafana, *arr, Home Assistant, Traefik, …) | `core` tunnel → Pangolin (on-prem) | Pangolin SSO/RBAC |
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

## Consequences

- **+** One tool for transport + SSO + RBAC; Teleport CT retired (RAM/vCPU freed); the admin door
  becomes self-hosted *identity*, not just CF Access PINs.
- **+** Phase 1 is add-only and reversible — Pangolin sits behind the existing tunnel, nothing on
  Cloudflare changes, break-glass untouched.
- **+** Leaves a clean, *optional* path to getting hostnames off Cloudflare later (#137) without
  committing to a VPS now.
- **~** No real loss of SSH session recording / cert-based SSH — planned via Teleport (BL-001) but
  never realized, so there is nothing operational to give up. Revisit a dedicated SSH-audit path
  only if access ever goes multi-user.
- **−** A VPS, *if* #137 ever warrants one, brings public surface + recurring cost + a box to patch;
  userspace WireGuard (Newt) is less performant than kernel WG. Deferred until then.
- **−** A per-hostname migration list (admin UIs → Pangolin) to track during cutover.
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
- **⚠ Main config delta for our model — TLS.** The stock install configures Traefik for Pangolin to
  *own* public ingress with Let's Encrypt (`httpChallenge` on :80/:443). **Behind cloudflared there is
  no public :80 for ACME** → the provisioner must reconfigure Traefik to serve plain **HTTP on :80 to
  cloudflared** and drop the LE `certResolver` + https-redirect. cloudflared provides the public TLS
  (same `noTLSVerify`/http origin pattern as the old Teleport ingress).
- **Gerbil/WireGuard is unused** in the on-prem-behind-cloudflared model (no Newt clients) — installed
  but idle. Confirms **no VPS / no Newt** needed for the SSO goal, as decided above.
- **Ingress integration.** The `core` tunnel points each admin hostname → the Pangolin CT's **Traefik
  :80**; Traefik routes by Host header to the backend and the **badger** plugin enforces Pangolin
  auth (redirect-to-login). So we add admin hostnames pointing at Traefik + define resources *in
  Pangolin*, rather than minting a per-service cloudflared ingress rule each time.

## Open decisions (resolve before this moves to Accepted)

1. **Final per-hostname migration list** — which admin UIs move to Pangolin in Phase 1 (operational;
   needs a per-hostname pass). The principle is set above; the exact list is TBD.
2. ~~SSH session recording~~ — **resolved (2026-06-20):** Teleport never went live, so there is
   nothing to drop. No SSH-audit path needed unless access goes multi-user.
3. **Pangolin Cloud vs self-hosted control plane** — default self-hosted; confirm.
4. ~~VPS provider/region~~ — **N/A for this ADR:** no VPS unless #137 (or a deliberate
   leave-Cloudflare decision) warrants one; scoped separately if so.

## Out of scope

- A standalone VPS stack — only if #137 (or a deliberate leave-Cloudflare decision) ever warrants
  one; its own ADR then. Not part of this rollout.
- #137 China VPN implementation (Hiddify / 3X-UI) — co-tenant of the VPS stack; tracked separately.
- Per-stack VLAN work and SSH-agent rollout details.
