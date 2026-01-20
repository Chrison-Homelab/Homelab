#!/bin/bash
# install-powershell.sh
#
# Installs PowerShell Core on Debian-based Proxmox nodes
# Uses snap package manager as Microsoft doesn't officially support Debian 13 yet
#
# Usage: bash <(curl -fsSL https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/install-powershell.sh)
#
# Requirements: snap package manager (installed by this script)
# Note: The official Microsoft repository method is commented out as it doesn't support Debian 13

# This would be the proper way to install PowerShell on Debian-based systems,
# but it's currently commented out because Microsoft doesnt support Debian 13 and Proxmox is based on this version.
# Download Microsoft repository configuration package
#. /etc/os-release && curl -sSL -O https://packages.microsoft.com/config/$ID/$VERSION_ID/packages-microsoft-prod.deb

# Install the Microsoft repository configuration package
#dpkg -i packages-microsoft-prod.deb
#rm packages-microsoft-prod.deb

# Install PowerShell
#apt-get update
#apt-get install -y powershell

# Alternative: Install PowerShell via Snap
apt update
apt install snapd
systemctl enable --now snapd.socket
ln -s /var/lib/snapd/snap /snap
snap install powershell --classic