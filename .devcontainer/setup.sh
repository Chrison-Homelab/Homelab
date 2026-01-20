#!/bin/bash
# setup.sh
#
# Dev container setup script
# Installs and configures tools for homelab development
#
# This script runs automatically when the dev container is created
# It installs:
# - Pester module for PowerShell testing
# - Ansible collections for infrastructure automation

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