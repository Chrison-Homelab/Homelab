# Proxmox Scripts

This directory contains automation scripts specifically designed for Proxmox VE nodes.

## Available Scripts

### Hardware Information Collection

- **inventory.sh / inventory.ps1**
  - Collects comprehensive hardware information in Markdown format
  - Suitable for documentation in Confluence or similar tools
  - Available in both Bash and PowerShell versions

- **hardware-info.sh**
  - Quick hardware overview script
  - Provides vendor and model information for key components
  - Lighter alternative to get-hardware-info.sh

- **get-hardware-info.sh**
  - Comprehensive hardware information collector
  - Includes detailed CPU, memory, storage, network, and GPU information
  - Useful for troubleshooting and detailed inventory

### Performance and Monitoring

- **proxmox-cpu-snapshot.sh**
  - Collects CPU configuration and usage for VMs and LXC containers
  - Useful for capacity planning and performance analysis
  - Includes per-thread CPU usage statistics

- **install-pulse-agent.sh / install-pulse-agent.ps1**
  - Installs, updates or removes the Pulse unified agent on a node
  - Adds the telemetry the Proxmox API cannot return — per-disk S.M.A.R.T., temperatures,
    ZFS/mdadm/Ceph detail, LXC filesystem breakdown — which is what the "Host telemetry not
    installed" banner in the Pulse UI refers to
  - Wraps the installer served by the Pulse server, so the agent is always version-matched
  - Takes the API token from `PULSE_API_TOKEN` / `--token-file` / `--token-stdin`, never argv
  - Available in both Bash and PowerShell versions

### System Configuration

- **install-powershell.sh**
  - Installs PowerShell Core on Proxmox nodes
  - Uses snap package manager (Microsoft doesn't support Debian 13 yet)
  - Required for running PowerShell scripts

- **setup-nfs-shares.sh / setup-nfs-shares.ps1**
  - Dynamically discovers and mounts NFS exports from a NAS
  - Automatically creates mount points and persists them in /etc/fstab
  - Available in both Bash and PowerShell versions
  - Supports custom NAS IP and name parameters

## Usage

For detailed usage instructions and examples, see [docs/Scripts.md](../../docs/Scripts.md).

### Quick Start

Most scripts can be executed directly from the repository:

```bash
# Using curl
bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/SCRIPT_NAME.sh)

# Using wget
bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/SCRIPT_NAME.sh)
```

For PowerShell scripts:

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/SCRIPT_NAME.ps1' -UseBasicParsing).Content"
```

## Testing

Scripts can be tested in the development container. See [TESTING.md](../../TESTING.md) for instructions.

## Requirements

### Common Requirements
- Proxmox VE (based on Debian 13)
- Root or sudo access

### Script-Specific Requirements
- **PowerShell scripts**: PowerShell Core (use install-powershell.sh to install)
- **NFS scripts**: nfs-common package (installed automatically by the scripts)
- **Hardware info scripts**: lscpu, dmidecode, lspci, ethtool, smartctl
- **Pulse agent**: smartmontools + lm-sensors (installed automatically by the script), a
  reachable Pulse server, and `PULSE_API_TOKEN` from `secrets.env`

## Development

When adding new scripts:
1. Follow the conventions in [.ai/conventions/bash.md](../../.ai/conventions/bash.md) or [.ai/conventions/powershell.md](../../.ai/conventions/powershell.md)
2. Include comprehensive header documentation
3. Add error handling and input validation
4. Test in the development container
5. Update documentation in [docs/Scripts.md](../../docs/Scripts.md)

## upgrade-guests.sh / .ps1

Apply pending OS package upgrades inside every **running** LXC on a node, non-interactively
(keeps local config on conflicts; per-guest log in `/var/log/homelab-upgrade.log`). `--dry-run`
only reports pending / security / reboot-required counts — the manual half of #436.

```bash
ssh root@hpe-01.homelab.chrison.internal 'bash -s -- --dry-run' < src/Proxmox/upgrade-guests.sh
ssh root@hpe-01.homelab.chrison.internal 'bash -s -- --reboot'  < src/Proxmox/upgrade-guests.sh
```

OS packages only. community-scripts apps update via their own `update`; container images via
`podman auto-update` (timer) or the docker member's compose pull; the node via `apt dist-upgrade`
plus a planned reboot. Those stay separate, deliberate acts.
