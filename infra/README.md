# Infrastructure as Code

This directory contains Infrastructure as Code (IaC) configurations for automating homelab infrastructure management.

## Directory Structure

- **ansible/** - Ansible playbooks and configurations for automation

## Ansible

Ansible is used for configuration management and automation across the homelab infrastructure.

### Quick Start

```bash
# Install Ansible
# macOS:
brew install ansible

# Windows:
pip install ansible

# Linux:
sudo apt install ansible  # Debian/Ubuntu
sudo yum install ansible   # RHEL/CentOS

# Install required collections
ansible-galaxy collection install community.synology
# ansible-galaxy collection install community.proxmox  # Coming soon
```

### Directory Structure

```
ansible/
├── inventory.yml           # Ansible inventory
├── group_vars/            # Group-specific variables
│   └── nas.yml           # NAS group variables
└── playbooks/            # Ansible playbooks
    └── nas_setup.yml     # NAS setup playbook
```

### Usage

```bash
cd ansible

# Test NAS connectivity
ansible -i inventory.yml nas -m ping

# Run NAS setup playbook
ansible-playbook -i inventory.yml playbooks/nas_setup.yml
```

## Supported Systems

### Synology DSM

Ansible automation for Synology NAS systems using the Community Synology collection.

**Collection**: `community.synology`  
**Installation**: `ansible-galaxy collection install community.synology`

### Proxmox VE (Coming Soon)

Ansible automation for Proxmox VE using the Community Proxmox collection.

**Collection**: `community.proxmox`  
**Installation**: `ansible-galaxy collection install community.proxmox`

## Development Environment

The Ansible inventory is pre-configured to work with the containerized DSM environment in `containers/dsm/`.

```bash
# Start DSM container
cd ../../containers/dsm
docker-compose up -d

# Test Ansible connectivity
cd ../../infra/ansible
ansible -i inventory.yml nas -m ping
```

## Conventions

Follow the Infrastructure as Code conventions defined in [.ai/conventions/iac.md](../.ai/conventions/iac.md).

## Adding New Playbooks

1. Create playbook in `ansible/playbooks/`
2. Follow naming convention: `<system>_<action>.yml`
3. Add documentation header with description, requirements, and usage
4. Test against containerized environments first
5. Update this README with playbook documentation

## Security Notes

⚠️ **Development Credentials**: The group_vars contain development credentials for use with containerized environments only. Never commit production credentials to the repository.

For production deployments:
1. Use Ansible Vault for sensitive data
2. Use environment variables
3. Use external secret management systems

## Resources

- [Ansible Documentation](https://docs.ansible.com/)
- [Ansible Synology DSM Collection](https://github.com/agaffney/ansible-synology-dsm)
- [Ansible Proxmox Collection](https://github.com/ansible-collections/community.proxmox)
- [Ansible Best Practices](https://docs.ansible.com/ansible/latest/user_guide/playbooks_best_practices.html)

## Future Additions

- Terraform configurations for cloud resources
- Pulumi configurations for multi-cloud management
- CloudFormation templates for AWS resources
- ARM templates for Azure resources
