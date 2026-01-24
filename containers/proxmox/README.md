# Containerized Proxmox VE

This directory contains Docker Compose configurations for running containerized Proxmox VE nodes for development and testing.

⚠️ **Platform Limitation**: These containers require x86_64 architecture and extensive Linux capabilities. They **do not work on macOS (ARM64)**.

## Platform Support

- ✅ **Linux** (x86_64 with kernel module support)
- ✅ **Windows** (x86_64 with WSL2 and kernel module support)
- ❌ **macOS** (ARM64 - not supported)

## Available Configurations

### compose.cluster.yml (Recommended)

A complete 3-node Proxmox VE cluster with optional Proxmox Datacenter Manager (PDM).

**Nodes:**
- `pve-1`: First Proxmox node (https://localhost:8006)
- `pve-2`: Second Proxmox node (https://localhost:8007)
- `pve-3`: Third Proxmox node (https://localhost:8008)
- `pdm`: Proxmox Datacenter Manager (https://localhost:8443)

**Shared Storage:**
- VM/LXC backups: `./VM-Backup` → `/var/lib/vz/dump`
- ISO files: `./ISOs` → `/var/lib/vz/template/iso`

### compose.yml

A simpler single-node configuration (if available).

## Quick Start

```bash
# Start the cluster
docker-compose -f compose.cluster.yml up -d

# Check status
docker-compose -f compose.cluster.yml ps

# View logs
docker-compose -f compose.cluster.yml logs -f pve-1

# Access first node
# Open browser to https://localhost:8006
# Login: root / 123
```

## Default Credentials

**Username**: root  
**Password**: 123

⚠️ **Security Note**: These are development credentials only. Never use these in production.

## Network Configuration

The cluster uses a dual-stack network (IPv4 and IPv6):

- **IPv4 Subnet**: 10.0.99.0/24
- **IPv6 Subnet**: fd00::/64
- **Gateway**: 10.0.99.99 (IPv4), fd00::99 (IPv6)

**Node IP Addresses:**
- pve-1: 10.0.99.1 / fd00::1
- pve-2: 10.0.99.2 / fd00::2
- pve-3: 10.0.99.3 / fd00::3
- pdm: 10.0.99.4 / fd00::4

## Port Mappings

| Service | Container Port | Host Port |
|---------|---------------|-----------|
| pve-1 Web UI | 8006 | 8006 |
| pve-1 SSH | 22 | 2222 |
| pve-1 Proxy | 3128 | 3128 |
| pve-2 Web UI | 8006 | 8007 |
| pve-2 SSH | 22 | 2223 |
| pve-2 Proxy | 3128 | 3129 |
| pve-3 Web UI | 8006 | 8008 |
| pve-3 SSH | 22 | 2224 |
| pve-3 Proxy | 3128 | 3130 |
| PDM Web UI | 8443 | 8443 |
| PDM SSH | 22 | 2225 |

## SSH Access

```bash
# SSH to first node
ssh root@localhost -p 2222
# Password: 123

# SSH to second node
ssh root@localhost -p 2223

# SSH to third node
ssh root@localhost -p 2224
```

## Use Cases

### Testing Proxmox Scripts

Test scripts against a real Proxmox environment without risk to production:

```bash
# Copy script to container
docker cp ../../src/Proxmox/inventory.sh pve-1:/root/

# Execute in container
docker exec -it pve-1 bash /root/inventory.sh
```

### Testing Cluster Operations

- Test cluster formation and management
- Test VM/LXC creation and migration
- Test backup and restore procedures
- Test high availability configurations

### Testing Ansible Playbooks

Test Proxmox automation with Ansible playbooks.

### Learning Proxmox

Safe environment to learn Proxmox features without affecting production systems.

## Shared Storage

The cluster has shared storage directories:

```bash
# VM/LXC backups
./VM-Backup → /var/lib/vz/dump

# ISO images
./ISOs → /var/lib/vz/template/iso
```

Place ISO files in `./ISOs` to make them available to all nodes.

## Container Requirements

These containers require extensive Linux capabilities:

- **cgroup**: private
- **cap_add**: ALL
- **security_opt**: 
  - seccomp=unconfined
  - apparmor=unconfined
  - systempaths=unconfined
- **device_cgroup_rules**: a *:* rwm

They also mount kernel modules from the host system.

## Cleanup

```bash
# Stop the cluster
docker-compose -f compose.cluster.yml down

# Remove containers and volumes (data will be lost)
docker-compose -f compose.cluster.yml down -v
rm -rf VM-Backup ISOs
```

## Troubleshooting

### Containers won't start

- **Check kernel modules**: Ensure necessary kernel modules are loaded
- **Check permissions**: Docker may need additional permissions
- **Check SELinux/AppArmor**: May need to be disabled or configured
- **Check logs**: `docker-compose -f compose.cluster.yml logs`

### Can't access web UI

- **Wait for startup**: First boot can take several minutes
- **Check container status**: `docker-compose -f compose.cluster.yml ps`
- **Check port conflicts**: Ensure ports 8006-8008, 8443 are not in use
- **Accept SSL certificate**: Browser will show warning for self-signed cert

### LXC/KVM not working

- **Check /dev/kvm**: May not be available in containers
- **Nested virtualization**: Full virtualization may be limited
- **Use containers**: Focus on LXC containers rather than VMs

### Cluster formation issues

- **DNS/Networking**: Ensure containers can resolve each other's hostnames
- **Time sync**: Ensure system time is synchronized
- **Clean state**: Remove containers and volumes, start fresh

## Limitations

- **Not production-ready**: For development and testing only
- **Limited virtualization**: Full KVM support may be restricted
- **Performance**: Slower than bare metal
- **Stability**: May be less stable than native installation
- **Feature parity**: Not all Proxmox features may work

## Resources

- [Containerized Proxmox Project](https://github.com/LongQT-sea/containerized-proxmox/)
- [Proxmox VE Documentation](https://pve.proxmox.com/wiki/Main_Page)
- [Proxmox VE API](https://pve.proxmox.com/wiki/Proxmox_VE_API)

## Alternative for macOS Users

If you're on macOS (ARM64), consider:
1. Using Debian 13 VM for script testing (see `../homelab/`)
2. Setting up actual Proxmox hardware for development
3. Using SSH tunneling to access remote Proxmox nodes
4. Using cloud-based x86_64 Linux instances
5. Focusing on script logic testing in the homelab container
