# Scripts

## Proxmox Scripts

All scripts are located in the `src/Proxmox/` directory and are designed to run on Proxmox VE nodes.

## PowerShell

### PowerShell Installer

Installs PowerShell Core on the Proxmox node for running PowerShell scripts. Uses snap package manager as Microsoft doesn't officially support Debian 13 yet.

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/install-powershell.sh)`

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/install-powershell.sh)`

### Inventory

Collects hardware information from Proxmox nodes in Markdown format. Output is formatted for easy copying into Confluence or other documentation. Available in both Bash and PowerShell versions.

**Bash version:**

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/inventory.sh)`

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/inventory.sh)`

**PowerShell version:**

**Direct execution:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/inventory.ps1' -UseBasicParsing).Content"
```

**Using wget (download first):**

```bash
wget https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/inventory.ps1 -O /tmp/inventory.ps1 && pwsh /tmp/inventory.ps1
```

### Hardware Information

Quick hardware overview script that collects vendor and model information for key components (CPU, mainboard, RAM, graphics, NICs).

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/hardware-info.sh)`

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/hardware-info.sh)`

### Detailed Hardware Information

Comprehensive hardware information collector that gathers detailed information about CPU, memory, storage, network, GPU, and system components. More detailed than the basic hardware-info.sh script.

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/get-hardware-info.sh)`

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/get-hardware-info.sh)`

### CPU Snapshot

Collects CPU configuration and usage statistics for VMs and LXC containers. Useful for capacity planning, performance analysis, and resource optimization.

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/proxmox-cpu-snapshot.sh)`

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/proxmox-cpu-snapshot.sh)`

### NFS Shares Setup

Dynamically discovers and mounts NFS exports from a NAS to a Proxmox node. Automatically creates mount points and persists them in /etc/fstab. Available in both Bash and PowerShell versions.

After a longer session with CoPilot, it turns out that setting NFS shares up on the proxmox node itself and sharing it out from there into LXC container is way better for performance. Plus it makes it easier to mount shares, no more messing around with NFS.
This also allows us to at some point add a SSD to the node and use this for caching.

**Bash version:**

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/setup-nfs-shares.sh)`

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/setup-nfs-shares.sh)`

**With parameters:**

```bash
curl -fsSL https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/setup-nfs-shares.sh | bash -s -- "192.168.1.100" "MyNAS"
```

**PowerShell version:**

**Direct execution:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content"
```

**With custom parameters:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-dev/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content" -- -NasIP "192.168.1.100" -NasName "MyNAS"
```
