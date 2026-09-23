# Backup stack — Proxmox Backup Server (#513)

The cluster's backup authority. **CT 2004 `pbs`** on nuc-01 runs Proxmox Backup Server 4;
the nightly cluster job dedupes and verifies guest backups into it.

## Why nuc-01, and the datastore-on-NFS choice

PBS lives on **nuc-01** (always-on, and deliberately *not* hpe-01 where most guests run), so
losing the busy node doesn't take the backups with it. The datastore is a **300 GB ext4 disk
image on NAS volume-2** (`mp0`), not a bind mount: volume-2 all-squashes every uid to 1024:100,
which an unprivileged bind mount would surface as `nobody` and break PBS's chunk-store ownership
and garbage collection. An image gives PBS a normal local ext4 it fully owns; the squash only
touches the single `.raw` file.

## What is IaC here, and what is still hand-made

`pbs.lxc.yaml` declares the guest and — the point of this stack today — pins its address:
`network.reservation` writes a DHCP reservation + local DNS record on `converge --apply`, so
the cluster storage and any client reach PBS by **`pbs.homelab.chrison.internal`** and a lease
change can never silently break every backup.

The engine has **no model yet** for a PBS datastore, `type pbs` cluster storage, or vzdump
backup *jobs*, so these remain hand-made (a follow-up once a backup-job provisioner exists):

| Piece | State | How it was made |
|---|---|---|
| CT 2004 guest + reservation/DNS | **declared** (`pbs.lxc.yaml`) | `converge --apply` |
| PBS install (v4, trixie no-sub repo) | hand, **adopted** | apt on the CT |
| Datastore `homelab` (`/mnt/datastore`) | hand | `proxmox-backup-manager datastore create` |
| GC (daily) + prune (7d/4w/6m) | hand | `datastore update --gc-schedule` / `prune-job create` |
| Cluster storage `pbs-homelab` (by hostname) | hand | `pvesm add pbs … --server pbs.homelab.chrison.internal` |
| Nightly job (02:00, 32 guests) | hand | `pvesh create /cluster/backup` |

The API token for the nodes is `pve@pbs!pve-nodes` (DatastoreAdmin on `homelab`).

## Not covered

- **Proxmox host/node backups** (#465) — PBS backs up guests, not the hypervisor OS.
- **Desktop-01's guests** — excluded from the nightly job while that node is WoL/asleep.

Validate members against `../../Infrastructure/schema/shape.schema.json`.
