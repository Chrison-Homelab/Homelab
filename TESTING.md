# Homelab Testing Container

This container provides a Debian 13 (Trixie) environment for testing Proxmox scripts, matching the base OS used by Proxmox.

## Quick Start

### Build and Run

```bash
# Build the container
docker build -t homelab-test .

# Run interactively
docker run -it --rm -v ./src/Proxmox:/homelab/scripts:ro homelab-test

# Or use docker-compose
docker-compose up -d debian-test
docker-compose exec debian-test bash
```

### Test Scripts

Inside the container:

```bash
# Test bash scripts
./scripts/inventory.sh
./scripts/install-powershell.sh
./scripts/setup-nfs-shares.sh

# Test PowerShell scripts  
pwsh ./scripts/inventory.ps1
pwsh ./scripts/setup-nfs-shares.ps1
```

## Features

- Debian 13 (Trixie) base - same as Proxmox
- PowerShell Core pre-installed
- Common system utilities (lscpu, dmidecode, etc.)
- NFS client tools
- Network utilities for testing

## NFS Testing

The docker-compose includes an optional NFS server for testing NFS mounting:

```bash
# Start both containers
docker-compose up -d

# Test NFS discovery from the debian container
docker-compose exec debian-test bash
showmount -e 192.168.100.10
```

## Development Workflow

1. Make changes to your scripts
2. Run the container: `docker-compose up -d debian-test`
3. Test your scripts: `docker-compose exec debian-test bash`
4. Scripts are mounted read-only from `./src/Proxmox`

## Cleanup

```bash
# Stop and remove containers
docker-compose down

# Remove volumes
docker-compose down -v

# Remove images
docker rmi homelab-test
```
