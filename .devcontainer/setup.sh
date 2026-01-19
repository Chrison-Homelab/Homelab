#!/bin/bash
set -e

echo "Installing Pester module in PowerShell..."
pwsh -c Install-Module Pester -Force
echo "Pester module installation complete."

echo "Setting up Ansible environment..."

ansible --version
ansible-galaxy collection install community.synology
#TODO: add Proxmox collection when needed

echo "Ansible setup complete."

echo "Setup script finished."