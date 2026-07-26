# 284 — Podman platform groundwork (`app: podman` + quadlet render/deploy)

Phase 0 of [#283](https://github.com/Chrison-Homelab/Homelab/issues/283), implementing
[ADR-0009](../adr/ADR-0009-podman-quadlet-migration.md). This is the reference for anyone
authoring a `app: podman` stack — in particular the **six things that bite**, all found by
provisioning a throwaway CT rather than by reading docs.

## How the path is composed

| Half | Owner | Why |
|---|---|---|
| Create | `CommunityScriptsCreator` + `ct/podman.sh` | Already runs `pct create` as **root over SSH**, which is the only way to set `keyctl` on an unprivileged LXC (the API token can't — see Azure's `ProxmoxLxcReconciler`). Already renders `var_unprivileged/nesting/keyctl/fuse` from `features:`. |
| Post-create | `PodmanProvisioner` | Converts the stock **rootful** Podman CT into the rootless quadlet host: masks the root socket, adds the user + subuid + linger, renders quadlets, drives `systemctl --user`. |

`ct/podman.sh` prompts twice (Portainer, Portainer Agent) — both answered `n` via the existing
`InstallStdin` mechanism. ADR-0009's control plane is systemd + git; a socket-driven GUI is
precisely what we're removing.

## Authoring a stack

```yaml
spec:
  app: podman
  features: { nesting: true, keyctl: true, fuse: true }
  config:
    # optional — defaults shown
    user: podman
    quadlets: <name>/quadlets     # relative to the stack dir
    subuidStart: 10000
    subuidCount: 50000
    autoUpdate: true              # podman-auto-update.timer, replaces Watchtower
    secrets:                      # podman secret name → secrets.env key
      mate_password: MATE_AUTH_PASSWORD
```

Quadlet files (`*.container`, `*.volume`, `*.network`, `*.pod`) go in the `quadlets` dir and are
rendered into `~<user>/.config/containers/systemd/`. No `*.kube` — ADR-0009 rules out any
`podman kube` path.

## The eleven gotchas

All were found by deploying, not by reading docs. **#1–4, #7 and #8 are fixed in the engine** —
you inherit those. **#5, #6, #9, #10 and #11 are rules you must follow when authoring quadlet
files**; nothing enforces them yet. #9 and #10 came out of the first real workload (Phase 1,
Leapmotor Mate on CT 6004); #11 came out of the first *multi-container* one (Phase 2a, youtarr
on CT 5114).

### 1. Rootless networking needs `/dev/net/tun` — an LXC has none

Rootless podman networks via **pasta** (or slirp4netns); both open `/dev/net/tun` to build the
tap device. Without it every container start dies:

```
pasta failed with exit code 1:
Failed to open() /dev/net/tun: No such file or directory
```

ADR-0009 anticipated that rootless has no routable per-container IP, but not this prerequisite.
The provisioner adds it host-side (`pct set` can't express raw `lxc.*` keys, so it appends to
`/etc/pve/lxc/<ctid>.conf`):

```
lxc.cgroup2.devices.allow: c 10:200 rwm
lxc.mount.entry: /dev/net dev/net none bind,create=dir
```

Bind the **directory**, not the `tun` file — binding the file fails when the container has no
`/dev/net` parent to mount onto. The host device node is mode `666`, so an **unprivileged** CT
can open it once bind-mounted: no privileged container, no host-networking workaround.

### 2. The create path silently drops `var_fuse`

The shape declared `fuse: true`; `ct/podman.sh` produced `features: nesting=1,keyctl=1`.
Reproduced on both provisions. The shape is the source of truth, so the provisioner reconciles
`features` itself via `pct set --features`, **merging** rather than replacing (an undeclared
`mount=nfs` is not ours to remove).

Rootless podman ended up on the `overlay` driver with `rootless=true`, so fuse-overlayfs wasn't
strictly needed here — but the shape must still be honoured, not quietly ignored.

### 3. `pct status` lies during a restart — never `pct reboot` + status-poll

`pct status` returns `running` the *instant* a reboot is requested, before shutdown has even
begun. A status-based wait therefore returns immediately, the next `pct exec` attaches to a
container that is mid-shutdown, and **blocks forever** — which in turn wedges the reboot, so the
CT never actually restarts. Two 10-minute converge hangs came from exactly this.

Use explicit `pct stop` → wait for `stopped` → `pct start` → poll
`systemctl is-system-running` for `running|degraded`, every probe `timeout`-wrapped so a wedged
`lxc-attach` can't hang converge.

### 4. `runuser` keeps cwd, and the rootless user can't read `/root`

`pct exec` lands in `/root`. `runuser -u podman -- podman …` inherits it and fails with
`cannot chdir to /root: Permission denied`. Insidious because `systemctl --user` works fine
(systemd sets its own cwd) while `podman secret create` fails. The deploy script does `cd /`
before anything runs as the user.

### 5. Quadlet `Exec=` goes through systemd specifier expansion — escape `%`

`Exec=... date -u +%FT%TZ ...` fails the unit outright:

```
Failed to resolve unit specifiers in '…date -u +%FT%TZ…': Invalid slot
Unit configuration has fatal error, unit will not be started.
```

`%F` isn't a valid systemd specifier. Any literal `%` in a quadlet must be written `%%`.

### 6. Boot survival requires `[Install] WantedBy=default.target` in the quadlet

The provisioner does **not** `systemctl --user enable` a unit — generated units can't be enabled
directly. The quadlet generator creates the `default.target` wants-symlink from the `[Install]`
section, so a quadlet without one exists, starts on demand, and silently never comes back after
a reboot. Stack authors must declare it.

### 7. Quadlets don't start at boot for 92s — `network-online.target` is never reached

The nastiest one, because it *looks* like it works. Podman injects
`Wants=/After=podman-user-wait-network-online.service` into every generated container unit
(containers/podman#22197), and that helper is literally:

```
ExecStart=/bin/sh -c until systemctl is-active network-online.target; do sleep 0.5; done
```

A stock community-scripts Debian LXC uses **ifupdown** (`networking.service`) and ships only
`systemd-networkd-wait-online` units, which aren't in play — so `network-online.target` is never
reached. The helper spins, times out after ~90s, **fails**, and only then does the container
start (`After=` is satisfied by the failure; `Wants=` doesn't propagate it).

Measured on CT 9900: booted `18:12:04`, `hello.service` active `18:13:36` — **92s late, every
boot**. It technically "survives a reboot", which is why a naive check passes.

The provisioner installs a small oneshot that makes the target genuinely reachable:

```ini
[Unit]
After=networking.service          # ifup blocks on DHCP → completion is real readiness
Wants=network-online.target
Before=network-online.target
[Service]
Type=oneshot
ExecStart=/bin/true
RemainAfterExit=yes
[Install]
WantedBy=multi-user.target
```

Masking the podman helper would also remove the delay, but throws away the network-readiness
guarantee the quadlets legitimately want.

### 8. A long create needs SSH keepalives, or a working create reports as failed

`CommunityScriptsCreator` holds **one** ssh session open for the entire template download + apt
install — 30+ minutes on a cold template. Without keepalives something in between resets the
idle-looking connection:

```
Read from remote host 192.168.179.2: Connection reset by peer
client_loop: send disconnect: Broken pipe
```

Observed provisioning CT 9900 from a laptop across VLANs (31 minutes in). The insidious part:
**the CT was actually created**, but the result never came back, so converge reported
`CREATE FAILED` for a create that had worked — leaving the next run to reconcile a CT the plan
believed didn't exist. `NodeExec` now sets `TCPKeepAlive=yes` +
`ServerAliveInterval=30`/`ServerAliveCountMax=10` (~5 min of tolerated silence). With keepalives
the same run completed in 6m20s.

The canonical path (self-hosted runner on the node LAN) is far less exposed than a laptop, which
is why this hadn't bitten before — it affects every provisioner, not just podman.

### 9. Podman does not create missing bind-mount source directories

Docker silently creates them; podman refuses:

```
Error: statfs /home/podman/leapmotor/certs: no such file or directory
```

With `Restart=always` this becomes a crash loop that ends in
`start request repeated too quickly` — six restarts in under two seconds on CT 6004. Any quadlet
using a host-path `Volume=` must ensure its source exists. `[Service] ExecStartPre=` is the place:

```ini
[Service]
ExecStartPre=/usr/bin/mkdir -p /home/podman/<app>/data /home/podman/<app>/certs
```

That also keeps the unit self-sufficient: a first start creates empty dirs, and a later data
migration seeds them.

### 10. `HealthCmd` cannot carry a quoted command — at all

Quadlet takes systemd's **word-split** value and rejoins it, so inner quoting is destroyed no
matter how you write it. Both forms verified broken on CT 6004:

| Written | What podman received |
|---|---|
| `HealthCmd=python -c "…'http://…'…"` | unterminated string → `/bin/sh: Syntax error: Unterminated quoted string` every 30s |
| `HealthCmd="python -c \"…\""` | the backslashes survived **literally** |

The first is the dangerous one: the container sits permanently **`unhealthy`** while the
application serves HTTP 200 perfectly well, so any monitoring keyed on health status lies.

A healthcheck here must be **quote-free**. This is a constraint on *quoting*, not on healthchecks
as a category — youtarr's works fine, because its image ships `curl` and the command needs no
quotes at all:

```ini
HealthCmd=curl --fail --silent --show-error --output /dev/null http://localhost:3011/api/health
```

So: check whether the image gives you a quote-free probe before giving up. If the check genuinely
needs quotes (a python one-liner, because the image ships no `curl`/`wget`), you cannot express it
as a quadlet — either drop it and rely on `Restart=always` for process death, or render a script
onto the host and call it quote-free (`HealthCmd=python /opt/health.py`). The latter needs the
provisioner to render non-quadlet assets, which it currently does not — a platform change, not a
quadlet tweak.

Also never put a **secret** in `HealthCmd`: it lands in the unit file. youtarr's MariaDB check
(`mysqladmin ping … -p$DB_ROOT_PASSWORD`) was dropped for that reason as much as the quoting.

### 11. `depends_on: condition: service_healthy` has NO quadlet or systemd equivalent

compose can gate one service on another being *healthy*. Quadlet cannot, and neither can systemd:
`After=`/`Requires=` wait for a unit to have **started**, not to be **ready**. Converting a
compose file with a health-gated `depends_on` therefore silently loses the gate.

Nor can you paper over it with a host-side wait:

```ini
# WRONG — youtarr-db is a podman-network DNS name, unresolvable from the CT
ExecStartPre=/usr/bin/sh -c until nc -z youtarr-db 3321; do sleep 2; done
```

The container names only resolve *inside* the podman network (via aardvark-dns), and
`ExecStartPre` runs on the CT.

Do it the systemd way instead — let it fail and retry:

```ini
[Service]
Restart=always
RestartSec=10
```

The dependent unit crash-loops harmlessly until its dependency accepts connections. Keep
`After=`/`Requires=` as well, so startup *order* is still right and the dependency is pulled in.

### Multi-container stacks need an explicit `.network`

Not a gotcha so much as a missing default: compose gives every service a shared network with
service-name DNS for free. **Quadlets do not.** Two containers that talk to each other need a
`.network` quadlet and `Network=<name>.network` on each, which is what enables aardvark-dns:

```ini
# youtarr.network
[Network]
NetworkName=youtarr
```

Verified on CT 5114: `youtarr-db.dns.podman` → `10.89.0.2`, resolvable from the youtarr container.

## Nested user namespaces (the risk ADR-0009 flagged)

An unprivileged LXC is itself userns-mapped — the host grants a window (conventionally
`0 100000 65536`), so **inside** the CT only uids `0..65535` exist. The host convention of giving
a rootless user `100000:65536` points outside that window and podman fails at first run
(`newuidmap: write to uid_map failed`).

**Before agonising over NFS ownership, check whether the export squashes.** Phase 2a expected
rootless writes to land under a different host uid than Docker's, leaving two owner uids on the
share — and it simply doesn't happen: the Synology export maps *every* incoming uid to `1024:100`,
so podman's writes are byte-identical to Docker's and Plex reads them unchanged. The userns layers
turn out to be irrelevant to ownership on that share. Verify with a probe file rather than
reasoning about the map.

Default is therefore `podman:10000:50000` — below `65534` (nobody), leaving room for real
accounts. The provisioner **verifies** rather than assumes, reading field 3 of
`/proc/self/uid_map` and failing loudly if `start + count` exceeds it. A container needing a uid
above the window (some images use `65534`) needs the CT's host-side idmap widened first; use
`:U`/`keep-id` where possible instead.

## Driving `systemctl --user` over `pct exec`

`pct exec` has no login session, no tty and no PAM environment, so `systemctl --user` can't find
its bus. The working incantation:

```bash
runuser -u podman -- env XDG_RUNTIME_DIR=/run/user/$UID_N \
  DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$UID_N/bus systemctl --user …
```

Both variables are required. `machinectl shell` is the alternative but drags in
systemd-container plus a working dbus activation path; this needs neither. `loginctl
enable-linger` must come first (it creates `/run/user/<uid>`), and the script waits — bounded —
for `/run/user/<uid>/bus` to appear, because `enable-linger` returns before `user@.service` has
finished starting.

## Idempotency

A marker at `~<user>/.homelab-managed` hashes every managed input **including quadlet file
content**, so editing a `.container` in the stack repo re-deploys on the next converge. It is
stamped **last** (mark-on-success), so any partial failure leaves no current marker and the next
converge re-runs the whole deploy. Verified live: the run that failed on gotcha #5 left no marker
and re-ran cleanly.

The marker also folds in a hash of the **generated deploy script**, not just its inputs. This
matters more than it sounds: the first cut hashed only inputs, so when the gotcha-#7 fix changed
`BuildDeploy`, converge reported `NOCHANGE` against the already-stamped CT 9900 and the fix never
landed. A provisioner change must invalidate the marker, or bug fixes silently no-op on exactly
the hosts that need them.

Podman secrets are **add-only** (`podman secret exists || create`) — rotation is an explicit
operator action, never a silent converge side effect. Values are piped via stdin, never argv, so
they can't leak into the process table.
