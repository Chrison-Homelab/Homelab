# Homelab

My Homelab

## Inventory Script

Grabs the Hardware for inventory purposes and outputs it in a MD format

**WGET:** `bash <(wget -qO- https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/inventory.sh)`

**CURL:** `bash <(curl -fsSL https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/inventory.sh)`

## Setup NFS Shares Script

After a longer session with CoPilot, it turns out that setting NFS shares up on the proxmox node itself and sharing it out from there into LXC container is way better for performance. Plus it makes it easier to mount shares, no more messing around with NFS.
This also allows us to at some point add a SSD to the node and use this for caching.

**WGET:** `bash <(wget -qO- https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/setup-ds1813-shares.sh)`

**CURL:** `bash <(curl -fsSL https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/setup-ds1813-shares.sh)`
