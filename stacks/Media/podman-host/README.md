# podman-host (CT 5114) — the Media stack's rootless Podman + quadlet host

The stack's **container host**, not a per-app box. Services arrive as extra quadlets in
[`quadlets/`](quadlets/). Established by ADR-0009 and Phase 2a ([#302](https://github.com/Chrison-Homelab/Homelab/issues/302)); replaces the Docker host **CT 5113**.

- **CT 5114** — in-block (Media owns 5100–5199), VLAN 1010, DHCP, node `hpe-01`
- **Rootless user** `podman`, subuid `10000:50000`
- **NFS** `/mnt/youtube` via host-level mount → `mp0`, plus the `ensure-data-mount.sh` hookscript
  carried over unchanged from CT 5113
- Read the superproject's `docs/plans/284-podman-platform.md` **before editing a quadlet**

## Current members

| Quadlet | Service | Notes |
|---|---|---|
| `youtarr.network` | shared network | **Required** — gives `youtarr` DNS resolution of `youtarr-db` |
| `youtarr-db.container` | MariaDB 10.3 | fixed uid 999 on local disk → `keep-id` |
| `youtarr.container` | Youtarr | writes video to the NFS share → **no** `:U`, **no** `UserNS=` |

## The three things that make this migration different from Phase 1

### 1. NFS through a third userns layer — and `:U` is dangerous here

```
NAS 192.168.179.11:/volume4/Volume-4/youtube      7.3T, 879G used
  → Proxmox host  /mnt/pve/ds1813-nfs-volume-4/youtube
  → LXC mp0       /mnt/youtube        (unprivileged → files appear as 65534/nobody)
  → rootless podman userns
  → container     /usr/src/app/data
```

**Never put `:U` on that volume.** It chowns recursively — 879 GB across NFS, rewriting
ownership on the NAS. (Contrast the Monitoring stack, #303, where `:U` is merely wasteful.)

**And no `UserNS=` on `youtarr`.** It runs as container-root, which the *default* rootless
mapping already sends to the `podman` user — one stable uid. `keep-id` would align the
*user's* uid instead and push container-root into the subuid range: strictly worse.

`youtarr-db` is the opposite case — a fixed uid (999) on local disk — so it *does* use
`keep-id:uid=999,gid=999`, with its data dir owned by `podman` on the host.

**Ownership does NOT change — verified, and better than predicted.** The worry was that
rootless (which cannot map container-root to CT root) would write under a different host uid
than Docker did, leaving two owner uids on the share.

It doesn't happen: **the NAS export already squashes every incoming uid to `1024:100`.** A probe
file written by the podman container landed as `1024:100` — byte-identical ownership to files the
old Docker setup wrote — and Plex (CT 5008, same share) read it back. So the three userns layers
turn out to be irrelevant to NFS *ownership*; what matters is only that the share is writable at
all. The NAS-side squashing that #208 would have introduced as "the real fix" is already in place.

### 2. No health-gated dependency

compose had `depends_on: { youtarr-db: { condition: service_healthy } }`. **Quadlet and systemd
have no equivalent** — `After=` waits for a unit to have *started*, not to be *ready*. A
host-side `ExecStartPre` wait is not an option either, because `youtarr-db` is a
podman-network DNS name and is unresolvable from the CT.

The readiness gap is covered the systemd way: `Restart=always` + `RestartSec=10`. If youtarr
starts before MariaDB accepts connections it exits and systemd retries until it can.

### 3. `YOUTUBE_OUTPUT_DIR` is a HOST path, not the mount target

The live container is told `YOUTUBE_OUTPUT_DIR=/mnt/youtube` while the data is mounted at
`/usr/src/app/data`. That looks like a bug and isn't ours to fix — it's how CT 5113 runs today
(verified against the running container before conversion). Preserved exactly. Do not
"correct" it to the container path.

## Cutover runbook

Videos need **no migration** — they live on the NFS share, which the new CT mounts at the same
path. Only ~172 MB of local state moves.

```bash
# 0. from the superproject: provision the host + quadlets
dotnet run --project Infrastructure/engine -- converge stacks/Media --apply

# 1. stop the OLD containers (CT 5113 host stays UP — the rollback path)
ssh root@hpe-01 'pct exec 5113 -- docker stop youtarr youtarr-db'

# 2. stop the NEW units before seeding their data
ssh root@hpe-01 'pct exec 5114 -- bash -lc "cd /; U=\$(id -u podman);
  runuser -u podman -- env XDG_RUNTIME_DIR=/run/user/\$U \
    DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/\$U/bus \
    systemctl --user stop youtarr.service youtarr-db.service"'

# 3. copy local state (database ~130M, jobs ~36M, server ~6M, config ~12K)
ssh root@hpe-01 'pct exec 5113 -- tar -C /opt/youtarr -cf - database config jobs server' > /tmp/youtarr.tar
ssh root@hpe-01 'pct exec 5114 -- mkdir -p /home/podman/youtarr'
ssh root@hpe-01 'pct exec 5114 -- tar -C /home/podman/youtarr -xf -' < /tmp/youtarr.tar
# keep-id maps container-999 to the podman user, so the DB dir must be owned by podman
ssh root@hpe-01 'pct exec 5114 -- chown -R podman:podman /home/podman/youtarr'
rm -f /tmp/youtarr.tar

# 4. start the new units (DB first; systemd's Requires=/After= also enforces this)
#    same runuser incantation as step 2, with `start`

# 5. verify, in this order
#    a. youtarr-db up; youtarr reaches it (DB_HOST=youtarr-db resolves via youtarr.network)
#    b. youtarr healthy (its healthcheck IS expressible — the image ships curl)
#    c. web UI on http://<5114-ip>:3087 shows the EXISTING channels/history
#    d. a NEW download lands on /mnt/youtube and Plex can read it   ← the real NFS test
#    e. unit survives an LXC reboot

# 6. leave CT 5113 STOPPED but NOT destroyed
ssh root@hpe-01 'pct stop 5113'
```

**Rollback:** `pct start 5113 && pct exec 5113 -- docker start youtarr-db youtarr`, then stop the
new units. The copy duplicates state rather than moving it, so CT 5113 keeps its own untouched
copy. The NFS share is shared between both, so nothing there is at risk either way.

CT 5113 is left stopped rather than destroyed on purpose — deleting it is a manual decision once
the new host has proven itself, and its DHCP reservation goes with it.
