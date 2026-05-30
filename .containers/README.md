# Containers

This directory contains Docker Compose configurations for development and testing environments.

## Directory Structure

- **homelab/** - Debian-based testing container for Proxmox scripts
- **dsm/** - Virtual Synology DSM container for development (Linux/Windows only)
- **proxmox/** - Containerized Proxmox VE for development (Linux/Windows only)

## Container Environments

### Homelab Testing Container

The homelab container provides a Debian 13 (Trixie) environment that matches the base OS used by Proxmox VE. It's used for testing Proxmox scripts in a safe, isolated environment.

**Quick Start:**
```bash
cd homelab
docker-compose up -d debian-test
docker-compose exec debian-test bash
```

See [TESTING.md](../TESTING.md) for detailed testing instructions.

### Virtual Synology DSM

For Linux and Windows systems, you can run a containerized Synology DSM instance for testing NAS-related scripts and Ansible playbooks.

**Platform Support:**
- ✅ Linux (x86_64)
- ✅ Windows (x86_64)
- ❌ macOS (ARM64 - not supported)

**Quick Start:**
```bash
cd dsm
docker-compose up -d
```

Access DSM at http://localhost:5000

### Containerized Proxmox VE

For Linux and Windows systems, you can run a containerized Proxmox VE cluster for testing infrastructure automation.

**Platform Support:**
- ✅ Linux (x86_64)
- ✅ Windows (x86_64)
- ❌ macOS (ARM64 - not supported)

**Quick Start:**
```bash
cd proxmox
docker-compose -f compose.cluster.yml up -d
```

Access Proxmox nodes:
- Node 1: https://localhost:8006
- Node 2: https://localhost:8007
- Node 3: https://localhost:8008
- PDM (Proxmox Datacenter Manager): https://localhost:8443

Default credentials: root / 123

## macOS Development

For macOS users (ARM64):
- Use the **homelab** container for script testing (fully supported)
- For Proxmox/DSM testing, consider:
  - Setting up a Debian 13 VM
  - Using a cloud-based development environment
  - Accessing actual hardware remotely

## Usage Guidelines

1. **Never test against production systems** - Always use containers or isolated VMs
2. **Test scripts locally first** - Use the homelab container before deploying to real nodes
3. **Keep containers updated** - Regularly pull latest images and rebuild
4. **Clean up resources** - Use `docker-compose down -v` to remove containers and volumes when done

## Development Workflow

1. Make changes to your scripts
2. Start the appropriate container environment
3. Test your changes
4. Iterate until working correctly
5. Deploy to actual infrastructure

## Requirements

- Docker Engine
- Docker Compose
- For Proxmox/DSM containers: Linux or Windows host with KVM support
- For homelab container: Any platform (macOS, Linux, Windows)

## Additional Resources

- [TESTING.md](../TESTING.md) - Detailed testing procedures
- [README.md](../README.md) - Main repository documentation
- [Virtual DSM Project](https://github.com/vdsm/virtual-dsm)
- [Containerized Proxmox Project](https://github.com/LongQT-sea/containerized-proxmox/)
