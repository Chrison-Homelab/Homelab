# Homelab Testing Container

This container provides a Debian 13 (Trixie) environment for testing Proxmox scripts. It matches the base OS used by Proxmox VE, ensuring scripts behave the same way in testing as they will in production.

## Quick Start

### Using Docker Compose (Recommended)

```bash
# Start the container
docker-compose up -d debian-test

# Access the container
docker-compose exec debian-test bash

# Test scripts (inside container)
./scripts/inventory.sh
./scripts/install-powershell.sh
pwsh ./scripts/inventory.ps1
```

### Using Docker Build Directly

```bash
# Build the image
docker build -t homelab-test .

# Run interactively
docker run -it --rm -v ./src/Proxmox:/homelab/scripts:ro homelab-test
```

## What's Included

- **Debian 13 (Trixie)** - Same base OS as Proxmox VE
- **PowerShell Core** - Pre-installed for PowerShell script testing
- **System Utilities** - lscpu, dmidecode, pciutils, util-linux
- **NFS Client Tools** - For testing NFS mounting scripts
- **Network Utilities** - net-tools, iproute2
- **Scripts Volume** - Proxmox scripts mounted from `../../src/Proxmox`

## NFS Testing

The compose file includes an optional NFS server for testing NFS mounting scripts:

```bash
# Start both containers
docker-compose up -d

# Test NFS from the debian container
docker-compose exec debian-test bash
showmount -e 192.168.100.10

# Test mounting
mkdir -p /test-mount
mount -t nfs -o vers=3 192.168.100.10:/exports /test-mount
```

## Development Workflow

1. **Make changes** to scripts in `src/Proxmox/`
2. **Start container**: `docker-compose up -d debian-test`
3. **Enter container**: `docker-compose exec debian-test bash`
4. **Test scripts**: Scripts are mounted at `/homelab/scripts/` (read-only)
5. **Iterate** until scripts work correctly
6. **Deploy** to actual Proxmox nodes

## Container Services

### debian-test
- **Base Image**: Debian 13 (Trixie)
- **Purpose**: Script testing environment
- **Network**: 192.168.100.0/24 (homelab-test network)
- **Scripts**: Mounted read-only from `../../src/Proxmox`

### nfs-test (Optional)
- **Base Image**: itsthenetwork/nfs-server-alpine
- **Purpose**: NFS server for testing mount scripts
- **IP Address**: 192.168.100.10
- **Exports**: /exports (backed by docker volume)
- **Port**: 2049

## Testing Specific Scripts

### Testing inventory.sh
```bash
docker-compose exec debian-test bash -c "./scripts/inventory.sh"
```

### Testing PowerShell Scripts
```bash
docker-compose exec debian-test bash -c "pwsh ./scripts/inventory.ps1"
```

### Testing NFS Scripts
```bash
# Start both containers
docker-compose up -d

# Test NFS setup script
docker-compose exec debian-test bash -c "./scripts/setup-nfs-shares.sh 192.168.100.10 nfs-test"
```

## Cleanup

```bash
# Stop containers
docker-compose down

# Remove volumes
docker-compose down -v

# Remove image
docker rmi homelab-test
```

## Limitations

- **No real hardware**: Some hardware-specific commands may not work (GPU detection, etc.)
- **No systemd**: Full systemd not available in containers
- **Limited PCI devices**: PCI device information is limited
- **No actual Proxmox**: This is Debian, not Proxmox VE (missing pveversion, qm, pct commands)

For testing Proxmox-specific commands, consider using the containerized Proxmox in `../proxmox/` (Linux/Windows only).

## Platform Support

- ✅ **macOS** (ARM64 and x86_64)
- ✅ **Linux** (x86_64, ARM64)
- ✅ **Windows** (x86_64)

## Troubleshooting

### Scripts not found
Ensure you're running docker-compose from the `.containers/homelab/` directory and that `src/Proxmox/` exists two levels up.

### Permission denied
Scripts are mounted read-only. If you need to modify them, do so in the `src/Proxmox/` directory on your host.

### NFS server not starting
On some systems, you may need to load the nfs kernel module: `sudo modprobe nfs`

## Additional Resources

- [Main TESTING.md](../../TESTING.md) - Comprehensive testing documentation
- [Proxmox Scripts README](../../src/Proxmox/README.md) - Script documentation
- [Scripts Usage Guide](../../docs/Scripts.md) - Detailed script usage
