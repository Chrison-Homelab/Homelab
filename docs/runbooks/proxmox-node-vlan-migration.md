# Runbook: move the Proxmox node management IPs to VLAN 1000

Moves all three nodes' management addresses off the legacy `192.168.178.0/23` subnet onto the
**Network Devices VLAN (1000, `10.0.0.0/16`)**, per the strategy on
[#37](https://github.com/Chrison-Homelab/Homelab/issues/37). Sub-issues:
[#341](https://github.com/Chrison-Homelab/Homelab/issues/341) nuc-01 ·
[#342](https://github.com/Chrison-Homelab/Homelab/issues/342) desktop-01 ·
[#343](https://github.com/Chrison-Homelab/Homelab/issues/343) hpe-01.

> **This is the most dangerous change in the migration.** Everything else moves one service;
> this moves the thing that *runs* the services, and it moves the cluster's own heartbeat with
> it. Read the whole runbook before starting.

**Verified against live state 2026-08-02.** Re-check anything marked ⚠ before you begin — the
config may have moved on.

---

## The core idea: run both addresses at once

The naive approach is "change the IP, fix corosync, hope". Don't. `vmbr0` is already
**VLAN-aware** (`bridge-vlan-aware yes`, `bridge-vids 2-4094`) on every node, so you can add a
**tagged sub-interface** `vmbr0.1000` carrying the new address *while the old address keeps
working*.

That single decision removes almost all the risk:

- Every step is verifiable **before** anything is taken away
- Corosync can be switched in **one atomic edit** with all nodes reachable on both addresses
- Rollback at any point is "delete the sub-interface", not "drive to the machine"

No switch reconfiguration is needed to get there: the node ports are already trunks
(guests use explicit VLAN tags), so tagged VLAN 1000 frames pass today.

---

## Preconditions

| Check | Command | Required |
|---|---|---|
| Cluster quorate | `pvecm status` | `Quorate: Yes`, 3/3 votes |
| **No HA resources** | `ha-manager config` | **empty** — if not, STOP and read *HA* below |
| NFS exports permit the target | `showmount -e nas.homelab.chrison.internal` | each volume lists `10.0.0.0/16` ✅ *(done 2026-08-02)* |
| Storage uses the DNS name | `grep server /etc/pve/storage.cfg` | `nas.homelab.chrison.internal` ✅ *(done)* |
| All storages active | `pvesm status` | 4/4 `active` on every node |
| Console access available | — | **physical or IPMI** — see *If you lose a node* |

**HA.** Today `ha-manager config` is empty and fencing is in standby, so there is **no fencing
risk**. If HA resources are ever added, a node whose corosync link flaps can be **fenced —
hard-rebooted** — mid-migration. Remove HA resources first, or don't do this.

### Address plan

VLAN 1000 is `10.0.0.1/16`, gateway `10.0.0.1`, DHCP `10.0.0.46–10.0.255.254`. Node addresses
must be **static and outside** the DHCP range:

| node | current | proposed | nodeid |
|---|---|---|---|
| nuc-01 | `192.168.179.1` | `10.0.0.11` | 1 |
| desktop-01 | `192.168.179.2` | `10.0.0.12` | 2 |
| hpe-01 | `192.168.179.3` | `10.0.0.13` | 3 |

Reserve them in UniFi so nothing else is ever handed them.

---

## Phase 0 — fix `/etc/hosts` first ⚠

**Proxmox resolves node names through `/etc/hosts`, not DNS.** Right now the files disagree:

```
hpe-01      knows only itself
nuc-01      knows nuc-01, desktop-01     — NOT hpe-01
desktop-01  knows nuc-01, desktop-01     — NOT hpe-01
```

That is a latent fault regardless of this migration, and it will bite during it. Before
touching addressing, make **every node list all three**, still on the old IPs:

```
192.168.179.1 nuc-01.pve.chrison.internal nuc-01
192.168.179.2 desktop-01.pve.chrison.internal desktop-01
192.168.179.3 hpe-01.pve.chrison.internal hpe-01
```

Verify from each node: `for n in nuc-01 desktop-01 hpe-01; do getent hosts $n; done`

---

## Phase 1 — add the new address alongside the old (per node, no downtime)

On each node, append to `/etc/network/interfaces`:

```
auto vmbr0.1000
iface vmbr0.1000 inet static
    address 10.0.0.11/16          # .12 desktop-01, .13 hpe-01
#   NO gateway line yet — the default route stays on vmbr0 until Phase 4
```

Apply without a reboot:

```bash
ifup vmbr0.1000
ip -4 addr show vmbr0.1000
```

**Do not add a `gateway` line here.** Two default routes will break outbound traffic in
confusing ways. The node keeps routing via `192.168.178.1` until Phase 4.

### Verify before moving on

```bash
# from each node, to each other node's NEW address
for ip in 10.0.0.11 10.0.0.12 10.0.0.13; do ping -c2 -W2 $ip; done
# and the NAS is reachable from the new subnet (exports already allow 10.0.0.0/16)
ping -c2 nas.homelab.chrison.internal
```

All nine node-to-node paths must work before Phase 3. If any fails, the switch is not passing
tagged VLAN 1000 to that port — fix that first.

---

## Phase 2 — repoint `/etc/hosts` at the new addresses

**Switch** each node entry to its new address on every node — do **not** add a second line per
host. Two lines for one hostname makes `getent` return whichever comes first, which is
ambiguous rather than redundant:

```
10.0.0.11 nuc-01.pve.chrison.internal nuc-01
10.0.0.12 desktop-01.pve.chrison.internal desktop-01
10.0.0.13 hpe-01.pve.chrison.internal hpe-01
```

This is safe *because* Phase 1 left both addresses live — the name now resolves to an address
that already works. Corosync is unaffected: `ring0_addr` holds literal IPs, not names.

Verify on every node: `for t in nuc-01 desktop-01 hpe-01; do getent hosts $t; done`

---

## Phase 3 — switch corosync (the one irreversible-feeling step)

> **Do this as a single atomic edit for all three nodes, not one at a time.**
>
> This contradicts the "one node at a time" note on the sub-issues, and deliberately. That
> guidance is right for *physical* moves, where a node genuinely disappears. Here every node is
> reachable on **both** addresses, so a rolling change would leave corosync in a mixed state
> across two subnets for an extended window — strictly worse than one clean flip. There is only
> **one corosync link** (`linknumber: 0`, no redundant ring), so minimise the time it is in flux.

Edit `/etc/pve/corosync.conf` **once, on any node** — pmxcfs replicates it:

```
nodelist {
  node { name: nuc-01      nodeid: 1  quorum_votes: 1  ring0_addr: 10.0.0.11 }
  node { name: desktop-01  nodeid: 2  quorum_votes: 1  ring0_addr: 10.0.0.12 }
  node { name: hpe-01      nodeid: 3  quorum_votes: 1  ring0_addr: 10.0.0.13 }
}
totem {
  config_version: 6        # ← MUST be incremented (currently 5) or the change is ignored
  ...
}
```

**Bumping `config_version` is not optional.** Corosync uses it to decide whether to accept a
reloaded config; forget it and the edit is silently ignored.

### ⚠ Writing the file is NOT enough — corosync must be RESTARTED

Saving `corosync.conf` triggers a *reload*, and **a reload cannot change a link address.**
Confirmed live on 2026-08-02:

```
[TOTEM] new config has different address for link 0
        (addr changed from 192.168.179.3 to 10.0.0.13). Internal value was NOT changed.
[CFG  ] Cannot configure new interface definitions: To reconfigure an interface it must be
        deleted and recreated. A working interface needs to be available to corosync at all times
```

Quorum stays green and everything *looks* fine — but `corosync-cfgtool -s` still shows the old
address. The file and the running ring have diverged, and the change only lands on the next
corosync restart or reboot. **Do not stop here**; a half-applied state is worse than either end.

```bash
corosync-cfgtool -s          # shows the RUNNING address — the file will lie to you
```

Restart corosync on **all three nodes at once**. A rolling restart works too (the cluster always
retains a quorate partition) but leaves individual nodes briefly quorate-less and the ring
negotiating across two subnets; simultaneous is cleaner and faster:

```bash
for n in nuc-01 desktop-01 hpe-01; do
  ssh $n 'nohup sh -c "sleep 3; systemctl restart corosync" >/dev/null 2>&1 &'
done
```

Then verify — and check the *ring*, not just quorum:

```bash
corosync-cfgtool -s   # addr = the NEW address on every node
pvecm status          # Quorate Yes, 3/3
touch /etc/pve/.w && rm /etc/pve/.w   # pmxcfs writable ⇒ quorum genuinely restored
pvecm updatecerts     # refresh cluster certs/known_hosts
```

If quorum does **not** return within ~30s, revert `corosync.conf` to the old addresses with
`config_version: 7` (higher again) and restart corosync — the old addresses are still live, so
recovery works. Note pmxcfs is read-only without quorum, so keep a copy of the old file
**outside** `/etc/pve` (e.g. `/root/corosync.conf.ROLLBACK`) before you start.

---

## Phase 4 — move the default route, one node at a time

Now the addressing actually changes. **This is the step that can lock you out**, so do it per
node with verification between, starting with **desktop-01** (least impact) and ending with
**hpe-01** (31 CTs + Home Assistant).

For each node:

1. Move `gateway 10.0.0.1` from the `vmbr0` stanza to `vmbr0.1000`, and remove the old
   `address 192.168.179.x/23` from `vmbr0`.
2. `reboot` — cleaner than `ifreload -a` here, because NFS mounts must re-establish from the
   new source address anyway. Budget one reboot per node.
3. Verify before the next node:

```bash
ip -4 addr; ip route                    # single default via 10.0.0.1
pvecm status                            # still 3/3 quorate
pvesm status                            # 4/4 active  ← the NFS export ACL check pays off here
pct list && qm list                     # guests back
```

> ⚠ **Guests are unaffected by all of this.** Their NICs carry explicit VLAN tags
> (`tag=1010`, `tag=1040`…), so a host's management VLAN is irrelevant to them. The only thing
> that changes for a guest is that its host briefly reboots.

---

## Phase 5 — sweep the references

The addresses are hard-coded in more places than the cluster:

| Where | What |
|---|---|
| `secrets.env` / `secrets.env.template` | `PROXMOX_BASE_URL` (`192.168.179.3:8006` is the self-signed fallback) |
| **Core cloudflared tunnel** | `proxmox.chrison.dev` → `https://192.168.179.1:8006` — **breaks at Phase 4**, not Phase 3. Repoint to `10.0.0.11`, or retire the name in favour of per-node names via Pangolin (`nuc-01.proxmox.…`) |
| `src/Proxmox/wake-node.sh` | baked-in MAC/address registry |
| Pangolin resource *Power Orchestrator* | → `192.168.179.1:8080`; repoint in `stacks/Core/pangolin.lxc.yaml`, then `converge stacks/Core --only pangolin --apply` (works since [#309](https://github.com/Chrison-Homelab/Homelab/issues/309)) |
| `~/.ssh/config` and this repo's tooling | `converge` and `proxmoxsharp` reach nodes by address |
| `docs/Devices.md`, `docs/Network.md` | node inventory |
| UniFi | old DHCP reservations for the legacy addresses |

Then remove the **old** `/etc/hosts` entries added in Phase 2.

---

## If you lose a node

Recovery differs by node, and this is why console access is a precondition:

- **hpe-01 and nuc-01 do not auto-boot after AC loss** — they hang at BIOS awaiting a console
  ([#237](https://github.com/Chrison-Homelab/Homelab/issues/237)). A clean `reboot` is fine, but
  if one wedges you need physical access.
- A node unreachable on **both** addresses means the interfaces file is wrong. Console in, restore
  `/etc/network/interfaces`, `systemctl restart networking`.
- Cluster split-brain: the surviving two hold quorum (2/3), so the cluster stays writable. Fix the
  third and rejoin; do **not** force `expected: 1`.

## Rollback

| Phase | Rollback |
|---|---|
| 1–2 | `ifdown vmbr0.1000`, delete the stanza. Nothing else touched. |
| 3 | Restore old `ring0_addr`s with a **higher** `config_version`. Old addresses still live. |
| 4 | Console in, restore `/etc/network/interfaces`, reboot. |

## Known gotchas, learned the hard way

- **`pvesm set --server` is refused** — `server` is a fixed parameter. `/etc/pve/storage.cfg`
  must be edited directly.
- **A config/mount mismatch silently marks storages `inactive`.** Proxmox compares
  `<server>:<export>` against the live mount source string. Data stays readable, but guest
  start/stop fails. Only a remount (practically, a reboot) clears it.
- **Stopped guests with `onboot=1` resurrect on reboot.** Four retired CTs came back this way on
  2026-08-02 and would have conflicted with their replacements. Before rebooting a node, check:
  `for c in $(pct list | awk 'NR>1{print $1}'); do …check tags+onboot…; done`
- **DNS is now a boot dependency** for storage, with no `/etc/hosts` fallback by choice. The
  resolver is the UniFi gateway — independent of the nodes, but not of the network.
