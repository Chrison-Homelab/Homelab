# ADR-0009 — Migrate Docker-in-LXC stacks to rootless Podman + quadlets (systemd + git as the control plane, no Komodo)

- **Status:** Accepted (direction) · implementation phased, not started
- **Date:** 2026-07-23
- **Deciders:** Chris
- **Relates to:** epic [#283](https://github.com/Chrison-Homelab/Homelab/issues/283),
  Phase-0 groundwork [#284](https://github.com/Chrison-Homelab/Homelab/issues/284),
  Leapmotor pilot [#285](https://github.com/Chrison-Homelab/Homelab/issues/285);
  [ADR-0001 IaC tooling](ADR-0001-iac-tooling.md) (self-contained stacks, Define→Discover→Converge),
  [ADR-0007 Pangolin](ADR-0007-pangolin-remote-access.md) (Docker EE — deliberately out of scope here),
  [ADR-0008 stack extraction / meta-repo](ADR-0008-stack-extraction-meta-repo.md) (quadlet files live in the stack submodules);
  supersedes the deferred **Komodo** orchestration idea (#106).

## Context

Several homelab services run as **Docker Compose stacks inside per-stack LXCs** — one thin
Docker host per stack (`app: docker` → community-scripts `ct/docker.sh`, or `get.docker.com` for
the Azure/Topaz path). This works, but it carries two costs we'd like to shed:

1. **The Docker daemon runs as root.** On an unprivileged LXC that's less alarming than on bare
   metal, but it is still a root-owned daemon with a socket, and several stacks mount
   `/var/run/docker.sock` back into containers (Watchtower, prune, gocd-agent, the never-deployed
   Komodo periphery) — the exact pattern we'd rather not have.
2. **The deploy unit is a compose stack, supervised by a daemon** — not a first-class,
   git-tracked, systemd-managed unit. That sits awkwardly against the repo's stated philosophy:
   *"infrastructure-as-code first, modular, idempotent, reversible, and minimal UI reliance"*
   (`CLAUDE.md`), and *"prefer building tooling over one-off manual ops; reproducible from bare
   metal."*

**Podman** addresses both, via two independent wins that pull in the same direction:

- **Rootless** — containers run as a non-root user (nested user namespace); no root daemon, no
  socket. This is the security driver.
- **Quadlets** — `.container`/`.pod`/`.network`/`.volume` files (Podman 4.4+, native `podman
  quadlet` CLI since 6.0) that a systemd generator turns into `.service` units. This changes *what
  the unit of management is*: from "a compose stack a daemon runs" to "a systemd unit generated
  from a file in git." systemd owns restart/ordering/boot/journald; **git owns the definition.**

### The tooling question resolves itself

We had earmarked **Komodo** as a future management stack (#106). Komodo manages **compose stacks**
over a Docker(-compatible) socket; it has **no concept of a quadlet**. Podman *can* expose a
Docker-compat socket and Komodo *can* drive it — but doing so keeps a root-socket-shaped, GUI-first
control plane and throws away the best half of Podman.

Crucially, **Komodo was never actually deployed** — the `stacks/Komodo` submodule is a near-empty
placeholder, parked as deferred work. So there is **zero sunk cost** in not adopting it. Once the
deploy unit is a quadlet, the "management stack" question largely dissolves: **systemd is the
supervisor, git is the config store**, remote control is `systemctl --user` over SSH, and the only
genuine gap is *observe* — filled by **Cockpit** (`cockpit-podman`, systemd-native, actually
understands units) and/or a **podman metrics exporter into the existing Prometheus/Grafana on
CT 4000**. "Bake our own" therefore shrinks from *a control-plane platform* to *a thin engine
provisioner + render/deploy step*, consistent with the `*Sharp` dogfood pattern.

### What the footprint sweep found (2026-07-23)

- The C# engine already renders `var_nesting` / `var_keyctl` / `var_fuse` / `var_unprivileged`
  (`CommunityScriptsCreator.cs`); every Docker LXC is already **unprivileged with nesting+keyctl** —
  exactly what rootless Podman wants. The missing piece is an **`app: podman`** provisioning path.
- **keyctl seam:** the Proxmox **API token** can't set `keyctl` on an unprivileged LXC (documented
  in the Azure `ProxmoxLxcReconciler`); existing Docker CTs get it only because `ct/docker.sh` runs
  `pct create` **as root over SSH**. ⇒ the podman provisioner must use the **pct/SSH path**, not the
  direct-API-token path.
- **Rootless networking** uses pasta/slirp4netns, not a bridge: no routable per-container LAN IP by
  default, and ports <1024 need a sysctl. This is why the host-net + BLE SmartHome stacks are a poor
  rootless fit.
- ⚠️ **Out-of-band security finding:** a live Cloudflare tunnel token is committed in cleartext in
  `stacks/Infrastructure/compose.yml`. Independent of this migration — rotate + move to
  `secrets.env`/Bitwarden regardless. (Tracked separately.)

## Decision

**Migrate the Docker-in-LXC stacks to rootless Podman + quadlets, with systemd + git as the
control plane and Cockpit + Prometheus for observe. Do not adopt Komodo. Go rootless from the
start on every clean stack, proving the fiddly plumbing on the smallest stack first.**

1. **Control plane = quadlet-native (not Komodo, not compose-on-podman).** The deploy unit is a set
   of `.container`/`.network`/`.volume` files living in each stack submodule (ADR-0008), rendered
   onto the host and managed as `systemctl --user` units. Observe = **Cockpit** on the podman hosts
   + a **podman exporter → Prometheus/Grafana (CT 4000)**. The `stacks/Komodo` submodule and #106
   are closed out as *superseded by quadlet-native*.

2. **Rootless from the start.** Each podman host runs a dedicated non-root `podman` user with a
   nested subuid/subgid range and `loginctl enable-linger`. This deliberately front-loads the
   trickiest plumbing (nested userns, linger, driving `systemctl --user` over `pct exec`) onto the
   **pilot**, where the blast radius is smallest.

3. **`app: podman` engine path (Phase 0, [#284](https://github.com/Chrison-Homelab/Homelab/issues/284)).**
   A bespoke provisioner (mirroring `PangolinProvisioner`), created via the **pct/SSH path**, that
   makes an unprivileged LXC with `features: nesting=1,keyctl=1,fuse=1`, installs `podman` +
   `podlet`, creates the `podman` user + linger, and renders/starts the stack's quadlets — replacing
   `docker compose up -d` in each `install.sh`. `podlet` converts existing compose as the starting
   point. Quadlet `Secret=` wires to `secrets.env`/Bitwarden.

4. **Side-by-side, per stack, reversible.** For each migrated stack, stand up a **new** podman LXC
   next to the Docker one (the media-stack-rebuild precedent), cut over, decommission the old CT.
   Rollback at any step is "keep the old CT."

5. **`podman auto-update` replaces Watchtower.** `AutoUpdate=registry` labels + a systemd timer
   remove the biggest `docker.sock` dependency natively; `podman system prune` timer replaces the
   prune container.

### Rollout order

| Phase | Scope | Issue |
|---|---|---|
| 0 | Platform groundwork: `app: podman` provisioner + quadlet render/deploy | [#284](https://github.com/Chrison-Homelab/Homelab/issues/284) |
| 1 | **Pilot — Leapmotor (CT 4100)**, side-by-side (proves the whole path) | [#285](https://github.com/Chrison-Homelab/Homelab/issues/285) |
| 2 | Media/youtarr (CT 5113, NFS-through-2-userns) + Monitoring (CT 4000, fixed-uid data dirs) | — |
| 3 | Observe: Cockpit + podman exporter → CT 4000; auto-update/prune timers | — |
| 4 | Resolve hard cases (see below) | — |

### Stack tiering

- **Clean (migrate):** Leapmotor CT 4100 · Media/youtarr CT 5113 (NFS write path through **two**
  userns layers — leans on the SynoSharp NFS findings) · Monitoring CT 4000 (data dirs chowned to
  fixed uids — grafana 472, loki/tempo 10001 — need `:U`/`keep-id`). All use ports >1024.
- **Hard / deferred:** SmartHome aircast CT 6002 + matter-server CT 6001 (`network_mode: host` for
  mDNS/RTP + Thread/BLE, `apparmor=unconfined`) — rootless buys ~nothing on an IoT-edge box; likely
  stays **rootful** Podman (still daemonless/quadlet, just not rootless). Infrastructure stack
  (docker.sock ×3) — resolved by dropping the Komodo path and replacing Watchtower/prune with
  podman-native; gocd-agent's socket need is the one holdout to design around.
- **Out of scope (deliberate):** Pangolin CT 2013 (Docker **EE** — an ADR-0007 product choice) ·
  Azure/Topaz CT 2009 (binds `:443`, separate `Topaz.Deploy` tooling, rootless-443 caveats already
  in-file).

## Alternatives considered

- **Podman + compose, keep Komodo** — rejected: least migration friction, but keeps a root-socket-
  shaped GUI control plane and skips quadlets/systemd entirely (the best half of Podman). Against the
  minimal-UI-reliance philosophy, and Komodo was never deployed so there's nothing to preserve.
- **Quadlet runtime + Komodo as a read-only dashboard** — rejected: Komodo can't manage quadlets,
  only observe containers over the socket; Cockpit does that job better and is systemd-native.
- **Rootful Podman quadlets everywhere** — rejected as the *default* (accepted only for the host-net
  SmartHome edge): it kills the daemon and is quadlet-native, but keeps containers running as root,
  which is the thing we set out to fix.
- **Stay on Docker** — rejected: leaves the root daemon + socket mounts and the daemon-supervised
  compose unit, both of which this ADR exists to remove.

## Consequences

- **+** Off the root daemon; no `docker.sock` on migrated stacks; containers run as a non-root user.
- **+** Deploy unit becomes a **git-tracked systemd unit** — restart/ordering/boot/journald for free;
  matches IaC-first / minimal-UI-reliance; reproducible from bare metal.
- **+** `podman auto-update` replaces Watchtower natively (removes the biggest socket dependency).
- **+** The "management stack" question dissolves — systemd + git + Cockpit, no Komodo to run/patch.
- **~** Observe changes shape: Cockpit + a podman exporter instead of a single GUI; folds into the
  existing Prometheus/Grafana rather than adding a control plane.
- **−** Real new complexity on day one: **nested user namespaces** (subuid/subgid inside the LXC's
  own userns map), **linger**, and driving `systemctl --user` over `pct exec`. Front-loaded onto the
  pilot to contain risk.
- **−** Rootless networking (pasta/slirp4netns) has no routable per-container IP and needs a sysctl
  for low ports — which is *why* the host-net SmartHome stacks are excluded/stay rootful.
- **−** NFS (youtarr) and fixed-uid data dirs (monitoring) need explicit userns/ownership handling.
- **−** A per-stack cutover list to track; Pangolin/Azure remain on Docker by design (mixed fleet
  during and after).

## Out of scope

- Pangolin (Docker EE, ADR-0007) and Azure/Topaz — not migrated.
- The committed Cloudflare tunnel token in `stacks/Infrastructure/compose.yml` — a security fix
  tracked separately, not gated on this migration.
- Any Kubernetes / `podman kube` path — quadlets only.
