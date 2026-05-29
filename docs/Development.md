# Development & Testing

How to set up a local environment and test changes before they touch real nodes.

## Local dev environments

Since my MacBook is ARM64, most homelab targets can't run natively. Use these:

**NFS shares:**
A generic Debian container, optionally with a second one beside it exporting an
NFS share or two. See [`containers/homelab/compose.yml`](../containers/homelab/compose.yml).

**Proxmox:**
No clean way to containerize on macOS (ARM64). On Linux & Windows you can use
[Containerized Proxmox](https://github.com/LongQT-sea/containerized-proxmox/) —
see [`containers/proxmox/compose.cluster.yml`](../containers/proxmox/compose.cluster.yml).
On macOS, Debian 13 is the way to go.

**Synology DSM:**
Same story — on Linux & Windows use [Virtual Synology DSM](https://github.com/vdsm/virtual-dsm).
See [`containers/dsm/compose.yml`](../containers/dsm/compose.yml).

**OrbStack (macOS):** `brew install orbstack` (still needs testing on the MacBook).

## Testing scripts

A Debian 13 (Trixie) container matches the Proxmox base OS for testing scripts
locally. See [`TESTING.md`](../TESTING.md) for the full workflow.

```bash
docker-compose up -d debian-test
docker-compose exec debian-test bash
```

## Infrastructure tooling

### Ansible

- **macOS:** `brew install ansible`
- **Windows:** `pip install ansible` — fiddly (correct Python, PATH, etc.).
  Honestly, just use macOS or Linux.

Then install the collections you need:

```bash
ansible-galaxy collection install community.synology
```

- Proxmox: [community.proxmox](https://github.com/ansible-collections/community.proxmox)
- Synology DSM: [ansible-synology-dsm](https://github.com/agaffney/ansible-synology-dsm)

### OpenTofu

```bash
tofu init
tofu plan
tofu apply
```

> Note: the OpenTofu CLI isn't installed on Windows yet — work on this from the
> MacBook for now.
