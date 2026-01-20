# Homelab

My Homelab infrastructure repository containing scripts, automation, and documentation for managing Proxmox VE nodes, Synology NAS, and related homelab services.

## Repository Structure

```
.
├── src/                    # Automation scripts
│   └── Proxmox/           # Proxmox VE management scripts
├── containers/            # Docker containers for development/testing
│   ├── homelab/          # Debian testing container (all platforms)
│   ├── dsm/              # Virtual Synology DSM (Linux/Windows only)
│   └── proxmox/          # Containerized Proxmox (Linux/Windows only)
├── infra/                # Infrastructure as Code
│   └── ansible/          # Ansible playbooks and configurations
├── docs/                 # Documentation
│   ├── Devices.md        # Hardware inventory
│   ├── Network.md        # Network architecture
│   └── Scripts.md        # Script usage guide
├── .ai/                  # AI assistant conventions and patterns
├── .devcontainer/        # VS Code dev container configuration
└── .github/              # GitHub workflows and configurations
```

## Quick Links

- **[Scripts Documentation](docs/Scripts.md)** - Usage guide for all automation scripts
- **[Testing Guide](TESTING.md)** - How to test scripts locally
- **[Network Architecture](docs/Network.md)** - VLAN and network design
- **[Device Inventory](docs/Devices.md)** - Hardware documentation

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

**MacOS Installation:**
Run `brew install ansible`. Its that simple on MacOS

**Windows Installation:**
Run `pip install ansible`. Sadly its not that simple. You need to add the right path, install the correct python, etc. Just use MacOS or Linux already. 

1. Install Ansible
2. Install Community.Synology collection

**Proxmox:**
[Ansible Module](https://github.com/ansible-collections/community.proxmox)

**Synology DSM:**
[Ansible Synology DSM](https://github.com/agaffney/ansible-synology-dsm)
`ansible-galaxy collection install community.synology`

## Testing

A Debian 13 container is provided for testing scripts locally before deploying to Proxmox nodes.

**Quick test environment:**

```bash
docker-compose up -d debian-test
docker-compose exec debian-test bash
```

See [TESTING.md](TESTING.md) for detailed testing instructions.

## Useful links

- [Synology's DSM API](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Package/FileStation/All/enu/Synology_File_Station_API_Guide.pdf)
- [Proxmox API](https://pve.proxmox.com/wiki/Proxmox_VE_API)