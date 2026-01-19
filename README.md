# Homelab

My Homelab

## Development Environment

In order to develop in an isolated dev environment (and not in prod ;-)) there is a few docker containers I spin up to test my scripts against:

**MacOS:**
run `brew install orbstack` to install orbstack and apparently be happy?
Not sure, needs some testing on my MacBook first.

**NFS shares:**
Just run a generic debian container, maybe set up a second one side by side that shares out a NFS export or two.
[see compose.yml](containers/homelab/compose.yml)

**Proxmox:**
Currently there is no easy way to containerize this. MacOS is ARM64 so you wont be able to run Proxmox in a container. Debian 13 is the way to go.
However, on Linux & Windows you can run a container
[Containerized Proxmox](https://github.com/LongQT-sea/containerized-proxmox/)
[see compose.cluster.yml](containers/proxmox/compose.cluster.yml)

**Synology DSM:**
Currently there is no easy way to containerize this. MacOS is ARM64 so you wont be able to run DSM in a container.
However, on Linux & Windows you can run a container
[Virtual Synology DSM](https://github.com/vdsm/virtual-dsm)
[see compose.yml](containers/dsm/compose.yml)

## Infrastructure as Code

### Ansible

**Installation:**

1. Install Ansible
`pip install ansible`
2. Install Community.Synology collection
`ansible-galaxy collection install community.synology`

**Proxmox:**
[Ansible Module](https://github.com/ansible-collections/community.proxmox)

**Synology DSM:**
[Ansible Synology DSM](https://github.com/agaffney/ansible-synology-dsm)

## Install PowerShell Script

Installs PowerShell Core on the Proxmox node for running PowerShell scripts.

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/install-powershell.sh)`

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/install-powershell.sh)`

## Inventory Script

Grabs the Hardware for inventory purposes and outputs it in a MD format

**WGET:** `bash <(wget -qO- https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/inventory.sh)`

**CURL:** `bash <(curl -fsSL https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/inventory.sh)`

Or use the powershell version:

**Using curl:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/inventory.ps1' -UseBasicParsing).Content"
```

**Using wget (download first):**

```bash
wget https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/inventory.ps1 -O /tmp/inventory.ps1 && pwsh /tmp/inventory.ps1
```

## Setup NFS Shares Script

After a longer session with CoPilot, it turns out that setting NFS shares up on the proxmox node itself and sharing it out from there into LXC container is way better for performance. Plus it makes it easier to mount shares, no more messing around with NFS.
This also allows us to at some point add a SSD to the node and use this for caching.

**WGET:** `bash <(wget -qO- https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/setup-ds1813-shares.sh)`

**CURL:** `bash <(curl -fsSL https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/setup-ds1813-shares.sh)`

Or run the powershell version:

**Direct execution:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content"
```

**With custom parameters:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content" -- -NasIP "192.168.1.100" -NasName "MyNAS"
```

## Useful links

- [Synology's DSM API](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Package/FileStation/All/enu/Synology_File_Station_API_Guide.pdf)
- [Proxmox API](https://pve.proxmox.com/wiki/Proxmox_VE_API)

## Testing

A Debian 13 container is provided for testing scripts locally before deploying to Proxmox nodes.

**Quick test environment:**

```bash
docker-compose up -d debian-test
docker-compose exec debian-test bash
```

See [TESTING.md](TESTING.md) for detailed testing instructions.
