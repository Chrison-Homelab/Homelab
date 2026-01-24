# Devices

This document contains inventory information for all homelab hardware devices.

## How to Use This Document

Use the **inventory.sh** or **inventory.ps1** scripts to automatically collect hardware information:

```bash
# Run on each Proxmox node
bash <(curl -fsSL https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/inventory.sh)

# Or using PowerShell
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/inventory.ps1' -UseBasicParsing).Content"
```

Copy the output and paste it into the appropriate section below.

## Proxmox Hosts

### Intel NUC

<!-- Paste inventory.sh output here -->

### HP EliteDesk

<!-- Paste inventory.sh output here -->

### Gaming PC

<!-- Paste inventory.sh output here -->

## Other Devices

### Network Equipment

<!-- Document switches, routers, access points -->

### Storage

<!-- Document NAS, SAN, or other storage devices -->

### Servers

<!-- Document any non-Proxmox servers -->

