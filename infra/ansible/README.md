# Ansible Configuration

Ansible configurations for automating homelab infrastructure management.

## Directory Structure

```
ansible/
├── inventory.yml           # Ansible inventory file
├── group_vars/            # Group-specific variables
│   └── nas.yml           # Variables for NAS group
└── playbooks/            # Ansible playbooks
    └── nas_setup.yml     # NAS setup and testing playbook
```

## Installation

### Prerequisites

- Ansible 2.9 or higher
- Python 3.6 or higher

### Install Ansible

**macOS:**
```bash
brew install ansible
```

**Windows:**
```bash
pip install ansible
```

**Linux:**
```bash
# Debian/Ubuntu
sudo apt update
sudo apt install ansible

# RHEL/CentOS
sudo yum install ansible

# Using pip (all platforms)
pip install ansible
```

### Install Required Collections

```bash
# Install Synology DSM collection
ansible-galaxy collection install community.synology

# TODO: Install Proxmox collection when needed
# ansible-galaxy collection install community.proxmox
```

## Inventory

The inventory file (`inventory.yml`) defines the hosts and groups for Ansible automation.

### Current Inventory

```yaml
all:
  hosts:
    nas:
      ansible_host: localhost
      ansible_port: 5000
      ansible_connection: local
```

This configuration is set up to work with the containerized DSM environment in `../../.containers/dsm/`.

### Testing Connectivity

```bash
# Ping test
ansible -i inventory.yml nas -m ping

# Gather facts
ansible -i inventory.yml nas -m setup
```

## Playbooks

### nas_setup.yml

Tests connectivity and gathers information from Synology DSM.

**Purpose**: Verify DSM API access and retrieve system information

**Usage:**
```bash
ansible-playbook -i inventory.yml playbooks/nas_setup.yml
```

**What it does:**
1. Checks if DSM is reachable via HTTP
2. Verifies the DSM API responds correctly
3. Authenticates with DSM using credentials from group_vars
4. Retrieves and displays DSM system information

## Group Variables

Variables for host groups are stored in `group_vars/`.

### nas.yml

Contains connection information for NAS hosts:

```yaml
dsm_host: "localhost:5000"
dsm_user: "dev_account"
dsm_password: "FZzRN4XCSRncu4"
```

⚠️ **Security Warning**: These are development credentials for use with containerized DSM only. Never use these credentials in production.

For production:
1. Use Ansible Vault to encrypt sensitive variables
2. Use environment variables
3. Use external secret management (HashiCorp Vault, AWS Secrets Manager, etc.)

## Development Workflow

### Testing with Containerized DSM

1. Start the DSM container:
   ```bash
   cd ../../.containers/dsm
   docker-compose up -d
   ```

2. Wait for DSM to fully boot (5-10 minutes on first start)

3. Test connectivity:
   ```bash
   cd ../../infra/ansible
   ansible -i inventory.yml nas -m ping
   ```

4. Run playbooks:
   ```bash
   ansible-playbook -i inventory.yml playbooks/nas_setup.yml
   ```

### Testing with Production Systems

1. Update `inventory.yml` with production host information
2. Update or encrypt `group_vars/nas.yml` with production credentials
3. Test connectivity before running playbooks
4. Use `--check` mode to preview changes:
   ```bash
   ansible-playbook -i inventory.yml playbooks/nas_setup.yml --check
   ```

## Creating New Playbooks

1. Create a new YAML file in `playbooks/`
2. Follow naming convention: `<system>_<action>.yml`
3. Include documentation header:
   ```yaml
   ---
   # Playbook: <name>
   # Description: <what it does>
   # Requirements: <collections, roles, etc.>
   # Usage: ansible-playbook -i inventory.yml playbooks/<name>.yml
   ```
4. Test against containerized environments first
5. Document the playbook in this README

## Ansible Collections

### Community Synology

Used for managing Synology DSM systems.

**Collection**: `community.synology`  
**Documentation**: [Ansible Synology DSM](https://github.com/agaffney/ansible-synology-dsm)  
**Install**: `ansible-galaxy collection install community.synology`

**Available Modules**:
- `community.general.synology_dsm_info` - Retrieve DSM information
- Additional modules for package management, user management, etc.

### Community Proxmox (Planned)

For managing Proxmox VE clusters.

**Collection**: `community.proxmox`  
**Documentation**: [Ansible Proxmox](https://github.com/ansible-collections/community.proxmox)  
**Install**: `ansible-galaxy collection install community.proxmox`

## Best Practices

1. **Use inventory groups** - Organize hosts by function (nas, proxmox, etc.)
2. **Store secrets securely** - Use Ansible Vault or external secret managers
3. **Test in development** - Always test against containerized environments first
4. **Use --check mode** - Preview changes before applying
5. **Document playbooks** - Include headers and update this README
6. **Version control** - Commit playbook changes with descriptive messages
7. **Follow conventions** - Adhere to [IaC conventions](../../.ai/conventions/iac.md)

## Troubleshooting

### Connection refused
- Verify the host is running and accessible
- Check firewall rules
- Verify port numbers in inventory

### Authentication failed
- Check credentials in group_vars
- Verify user has necessary permissions
- Check if API access is enabled

### Module not found
- Install required collections: `ansible-galaxy collection install <collection>`
- Check collection is listed in requirements.yml (if using one)

### Slow execution
- Reduce gather_facts if not needed: `gather_facts: false`
- Use async tasks for long-running operations
- Increase parallelism with `-f` flag

## Security Considerations

### For Development
- Use dedicated development credentials
- Never reuse development credentials in production
- Keep containerized environments isolated

### For Production
- **Use Ansible Vault** for encrypting sensitive variables
- **Limit access** with proper file permissions (600 for vault files)
- **Rotate credentials** regularly
- **Use SSH keys** instead of passwords where possible
- **Audit playbook runs** with logging enabled
- **Test in staging** before production deployment

### Encrypting Sensitive Data

```bash
# Create encrypted variable file
ansible-vault create group_vars/nas_prod.yml

# Edit encrypted file
ansible-vault edit group_vars/nas_prod.yml

# Run playbook with vault
ansible-playbook -i inventory.yml playbooks/nas_setup.yml --ask-vault-pass
```

## Resources

- [Ansible Documentation](https://docs.ansible.com/)
- [Ansible Best Practices](https://docs.ansible.com/ansible/latest/user_guide/playbooks_best_practices.html)
- [Ansible Vault](https://docs.ansible.com/ansible/latest/user_guide/vault.html)
- [Community Synology Collection](https://github.com/agaffney/ansible-synology-dsm)
- [Community Proxmox Collection](https://github.com/ansible-collections/community.proxmox)
- [Synology DSM API Guide](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Package/FileStation/All/enu/Synology_File_Station_API_Guide.pdf)
