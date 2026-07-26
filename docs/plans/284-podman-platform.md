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

## The seven gotchas

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

## Nested user namespaces (the risk ADR-0009 flagged)

An unprivileged LXC is itself userns-mapped — the host grants a window (conventionally
`0 100000 65536`), so **inside** the CT only uids `0..65535` exist. The host convention of giving
a rootless user `100000:65536` points outside that window and podman fails at first run
(`newuidmap: write to uid_map failed`).

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
