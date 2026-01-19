# Network Architecture

At the core of my network is a Unifi Cloud Gateway which manages all the networking.

## Vlans

### Consumer

**CIDR:** 10.20.0.0/16
This network hosts all consumer devices, including guests (aka friends) and VPN connections from outside.
In rare cases it is also fine to expose other devices like a SmartTV if the app requires it.

### Homelab

**CIDR:** 10.10.0.0/16
Exclusively reserved for my Homelab.
Ideally each Proxmox node gets its own sub-segment, but this is currently not implemented yet.

### IOT

**CIDR:** 10.40.0.0/16
Hosts all IOT devices to physically isolate them in their own litte network.

### Network Devices

**CIDR:** 10.0.0.0/16
Hosts all infrastructure related devices like Switches, NAS, etc.

### Old Network

**CIDR:** 192.168.178.0/23
This network is an old relict from when I still had a Synology Router.
It exists purely for legacy reasons and will die as soon as the transition to the new vlan based architecture is done.
