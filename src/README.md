# Source Scripts

This directory contains all automation scripts for the homelab infrastructure.

## Directory Structure

- **Proxmox/** - Scripts for Proxmox VE node management and configuration

## Script Categories

### Proxmox Scripts

All Proxmox scripts are located in the `Proxmox/` subdirectory. See the [Scripts documentation](../docs/Scripts.md) for detailed usage instructions.

Available scripts:
- **inventory.sh / inventory.ps1** - Hardware inventory collection in Markdown format
- **hardware-info.sh** - Quick hardware overview (vendor/model info)
- **get-hardware-info.sh** - Comprehensive hardware information collector
- **proxmox-cpu-snapshot.sh** - CPU configuration and usage statistics for VMs/LXC
- **install-powershell.sh** - Install PowerShell Core on Proxmox nodes
- **setup-nfs-shares.sh / setup-nfs-shares.ps1** - Dynamic NFS mount configuration

## Usage

Each script includes header documentation with usage instructions, requirements, and examples. Scripts can be:
- Downloaded and run locally
- Executed directly via curl/wget (see [Scripts.md](../docs/Scripts.md))
- Run from the testing container (see [TESTING.md](../TESTING.md))

## Development

When adding new scripts:
1. Include a comprehensive header with description, usage, and requirements
2. Follow the conventions in [.ai/conventions/](../.ai/conventions/)
3. Update [docs/Scripts.md](../docs/Scripts.md) with the new script
4. Test in the development container before deploying to production nodes
