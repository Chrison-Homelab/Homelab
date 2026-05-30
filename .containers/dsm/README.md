# Virtual Synology DSM Container

This directory contains a Docker Compose configuration for running a virtual Synology DSM instance for development and testing.

⚠️ **Platform Limitation**: This container requires x86_64 architecture and KVM support. It **does not work on macOS (ARM64)**.

## Platform Support

- ✅ **Linux** (x86_64 with KVM)
- ✅ **Windows** (x86_64 with KVM/Hyper-V)
- ❌ **macOS** (ARM64 - not supported)

## Quick Start

```bash
# Start DSM container
docker-compose up -d

# Check logs
docker-compose logs -f

# Access DSM
# Open browser to http://localhost:5000
```

## Container Configuration

- **Image**: vdsm/virtual-dsm
- **Disk Sizes**: 
  - Primary disk: 10GB
  - Secondary disk: 10GB
  - Tertiary disk: 10GB
- **Port**: 5000 (mapped to host port 5000)
- **Storage Volumes**:
  - `./dsm` - Primary storage
  - `./example2` - Secondary storage
  - `./example3` - Tertiary storage

## First-Time Setup

1. Start the container: `docker-compose up -d`
2. Wait for DSM to boot (may take several minutes on first run)
3. Access http://localhost:5000 in your browser
4. Follow the DSM installation wizard
5. Create an admin account

## Use Cases

### Testing Ansible Playbooks

The virtual DSM instance is perfect for testing Ansible playbooks without affecting production NAS systems.

```bash
# From the repository root
cd infra/ansible
ansible-playbook -i inventory.yml playbooks/nas_setup.yml
```

The default inventory in `infra/ansible/inventory.yml` is configured to connect to this container.

### Testing NFS Exports

You can configure NFS exports in DSM and test mounting them from the homelab container or Proxmox scripts.

### API Development

Test scripts that interact with the Synology DSM API without risk to production systems.

## Ansible Integration

The default Ansible inventory is configured for this container:

```yaml
all:
  hosts:
    nas:
      ansible_host: localhost
      ansible_port: 5000
      ansible_connection: local
```

Default credentials in `infra/ansible/group_vars/nas.yml`:
- Host: localhost:5000
- User: dev_account
- Password: FZzRN4XCSRncu4

⚠️ **Security Note**: These are development credentials. Never use these in production.

## Persistence

DSM data is stored in docker volumes (`./dsm`, `./example2`, `./example3`). The container state persists across restarts unless you remove these volumes.

## Cleanup

```bash
# Stop the container
docker-compose down

# Remove container and volumes (data will be lost)
docker-compose down -v
rm -rf dsm example2 example3
```

## Troubleshooting

### Container won't start

- **Check KVM**: Ensure KVM is available: `ls -la /dev/kvm`
- **Check permissions**: Your user may need to be in the `kvm` group
- **Check resources**: Ensure Docker has sufficient resources allocated

### Can't access DSM on port 5000

- **Check container status**: `docker-compose ps`
- **Check logs**: `docker-compose logs dsm`
- **Wait for boot**: First boot can take 5-10 minutes
- **Check firewall**: Ensure port 5000 is not blocked

### DSM installation fails

- **Insufficient disk space**: Check available disk space on your host
- **Network issues**: DSM may need internet access to download updates
- **Try rebuilding**: `docker-compose down -v && docker-compose up -d`

## Performance Considerations

- **Stop when not in use**: The container uses significant resources
- **Graceful shutdown**: Use `docker-compose stop` (not `down`) to preserve state
- **Stop grace period**: 2 minutes is configured to allow proper shutdown

## Limitations

- Not a full Synology NAS - some features may be limited
- Requires x86_64 architecture (no ARM64 support)
- Requires KVM/virtualization support
- Performance may be slower than physical hardware

## Resources

- [Virtual DSM Project](https://github.com/vdsm/virtual-dsm)
- [Synology DSM API Guide](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Package/FileStation/All/enu/Synology_File_Station_API_Guide.pdf)
- [Ansible Synology Collection](https://github.com/agaffney/ansible-synology-dsm)

## Alternative for macOS Users

If you're on macOS (ARM64), consider:
1. Using a cloud-based x86_64 Linux VM
2. Setting up a physical Synology device for development
3. Using SSH tunneling to access a remote development DSM instance
4. Skipping DSM-specific testing and focusing on script logic
