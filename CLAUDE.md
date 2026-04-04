# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Personal homelab infrastructure-as-code repository managing Proxmox hypervisors, Synology NAS devices, and associated monitoring/networking. The core philosophy: **infrastructure-as-code first, modular, idempotent, reversible, and minimal UI reliance**. Always assume multi-node and multi-NAS environments.

## Key Commands

### Testing Scripts Locally (Debian test container)

```bash
# Start the test container (mounts src/Proxmox as read-only)
cd containers/homelab
docker compose up -d debian-test

# Exec into it and run scripts
docker compose exec debian-test bash
./scripts/inventory.sh
pwsh ./scripts/inventory.ps1

# Start NFS test server alongside (for NFS mount testing)
docker compose up -d nfs-test
# NFS server available at 192.168.100.10 inside the test network
```

### Monitoring Stack

```bash
cd infra/docker/monitoring
# Requires .env file with: RADARR_API_KEY, RADARR_URL, SONARR_API_KEY, SONARR_URL,
#   PROWLARR_API_KEY, PROWLARR_URL, GRAFANA_PORT, GF_SECURITY_ADMIN_USER, GF_SECURITY_ADMIN_PASSWORD
docker compose up -d
# Prometheus: http://localhost:9090
# Grafana:    http://localhost:3000 (or $GRAFANA_PORT)
# SNMP:       http://localhost:9116
# Servarr:    http://localhost:9707
```

### Ansible

```bash
# Install collections (already done in devcontainer post-create)
ansible-galaxy collection install community.synology

# Run playbooks
ansible-playbook infra/ansible/playbooks/nas_setup.yml -i infra/ansible/inventory.yml
```

### OpenTofu (Synology IaC)

```bash
cd infra/opentofu/synology-nas
tofu init
tofu plan
tofu apply
```

## Architecture

### Directory Layout

- **`src/Proxmox/`** — Bash and PowerShell scripts deployed directly to Proxmox nodes. Scripts exist in both `.sh` and `.ps1` variants with equivalent functionality.
- **`infra/ansible/`** — Ansible playbooks/inventory for DSM (Synology) configuration management. Credentials for local test DSM are in `group_vars/nas.yml`.
- **`infra/opentofu/synology-nas/`** — OpenTofu IaC for Synology NAS resources (currently exploratory).
- **`infra/docker/monitoring/`** — Docker Compose monitoring stack: SNMP Exporter → Prometheus → Grafana, plus Servarr Exporter for *arr apps.
- **`containers/homelab/`** — Debian 13 (Trixie) test container matching the Proxmox OS. Used for local validation of `src/Proxmox/` scripts.
- **`containers/proxmox/`** — Containerized Proxmox for local dev (requires `/dev/kvm`, Linux only).
- **`containers/dsm/`** — Virtual DSM container (Synology) for local testing, exposed on port 5000.
- **`docs/`** — Network architecture, device inventory, and script documentation.
- **`.devcontainer/`** — VS Code Dev Container (Ubuntu base) with PowerShell, Ansible, Terraform, and Docker extensions pre-configured.
- **`.github/agents/`** — Agent persona definitions for AI-assisted development workflows.

### Network Architecture

- **Homelab VLAN**: `10.10.0.0/16`
- **Consumer VLAN**: `10.20.0.0/16`
- **IoT VLAN**: `10.40.0.0/16`
- **Network Devices**: `10.0.0.0/16`
- **Legacy** (being deprecated): `192.168.178.0/23`
- Managed via Unifi Cloud Gateway

### Scripting Conventions

- Every script in `src/Proxmox/` has both a `.sh` (Bash) and `.ps1` (PowerShell Core) version — keep them functionally in sync.
- NFS mounts are configured at the **Proxmox host level** (not inside LXC containers) for performance and FS-Cache potential.
- Scripts are designed to be fetched and run remotely (wget/curl from a URL) as documented in `docs/Scripts.md`.
