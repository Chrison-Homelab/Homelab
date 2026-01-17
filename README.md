# Homelab

My Homelab

## Install PowerShell Script

Installs PowerShell Core on the Proxmox node for running PowerShell scripts.

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/install-powershell.sh)`

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/install-powershell.sh)`

## Inventory Script

Grabs the Hardware for inventory purposes and outputs it in a MD format

**WGET:** `bash <(wget -qO- https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/inventory.sh)`

**CURL:** `bash <(curl -fsSL https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/inventory.sh)`

## Setup NFS Shares Script

After a longer session with CoPilot, it turns out that setting NFS shares up on the proxmox node itself and sharing it out from there into LXC container is way better for performance. Plus it makes it easier to mount shares, no more messing around with NFS.
This also allows us to at some point add a SSD to the node and use this for caching.

**WGET:** `bash <(wget -qO- https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/setup-ds1813-shares.sh)`

**CURL:** `bash <(curl -fsSL https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/setup-ds1813-shares.sh)`

Or run the powershell version:

**Direct execution:**
```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content"
```

**With custom parameters:**
```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content" -- -NasIP "192.168.1.100" -NasName "MyNAS"
```
