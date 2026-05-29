# Network Architecture

At the core is a **UniFi Cloud Gateway** managing all networking. Networks/VLANs,
WANs, and WLANs below are auto-documented from the UniFi MCP (read-only discovery,
BL-009). **Last updated:** 2026-05-29. Device hardware is in [Devices.md](Devices.md).

## VLANs / Networks

| Network | VLAN | Subnet | Domain | DHCP range |
|---|---|---|---|---|
| **Network Devices** | 1000 | 10.0.0.1/16 | network.chrison.internal | 10.0.0.46 – 10.0.255.254 |
| **Homelab** | 1010 | 10.10.0.1/16 | homelab.chrison.internal | 10.10.0.46 – 10.10.255.254 |
| **Consumer** | 1020 | 10.20.0.1/16 | — | 10.20.0.46 – 10.20.255.254 |
| **IOT** | 1040 | 10.40.0.1/16 | iot.chrison.internal | 10.40.0.46 – 10.40.255.254 |
| **Old Network** (legacy) | untagged | 192.168.178.1/23 | localdomain | 192.168.178.11 – 192.168.179.254 |
| **One-Click VPN** | — | 192.168.9.1/24 | — | 192.168.9.6 – 192.168.9.254 |

### Network Devices — `10.0.0.0/16` (VLAN 1000)
Infrastructure: switches, APs, NAS. *(Network.md previously omitted the VLAN ID —
it is **1000**.)*

### Homelab — `10.10.0.0/16` (VLAN 1010)
Exclusively for the homelab (Proxmox guests). Per-node sub-segmentation is not yet
implemented.

### Consumer — `10.20.0.0/16` (VLAN 1020)
Consumer devices, guests, and inbound VPN. Personal devices (MacBooks, iPhones,
Apple TVs, Alexas) **should** live here but are currently still on Old Network.

### IOT — `10.40.0.0/16` (VLAN 1040)
Physically isolated IoT devices (smart plugs, washer, Zigbee/Wi-Fi sensors).

### Old Network — `192.168.178.1/23` (legacy, being retired)
Relic of the old Synology-router era. Still hosts the Proxmox nodes, the NAS,
Home Assistant, several *arr LXCs, and most personal devices — migration tracked
in BL-002 / [Services.md](Services.md). *(Backlog previously referenced
`192.168.179.x`; the actual definition is `192.168.178.1/23`, spanning
`.178.x`–`.179.x`.)*

## WAN

Dual WAN on the gateway:

| WAN | Purpose |
|---|---|
| Quic.nz | Primary internet |
| Internet 2 | Secondary internet |

## WLANs (SSIDs)

Passphrases are intentionally **not** documented here (the UniFi read API exposes
them; they are kept out of version control).

| SSID | Bands | Security | Maps to network | Enabled |
|---|---|---|---|---|
| **Blackbox** | 2.4 + 5 GHz | WPA2 | ⚠️ Old Network (legacy) | Yes |
| **Blackbox_IOT** | 2.4 GHz | WPA2 (enhanced IoT) | IOT (1040) | Yes |
| **89D Tao-Simon** | 2.4 + 5 GHz | WPA2/WPA3 transition | Consumer (1020) | Yes |
| **UniFi Identity** | 2.4 + 5 GHz | WPA-Enterprise (RADIUS) | — | No (disabled) |

> ⚠️ **Migration blocker:** the primary `Blackbox` SSID still places wireless
> clients on the **Old Network**. Until it maps to Consumer/Homelab, personal
> devices can't fully leave the legacy subnet (BL-002).

## Notes

- Managed via a UniFi Cloud Gateway (gateway MAC `1c:6a:1b:43:62:57`).
- All internal VLANs use `*.chrison.internal` domains (except Consumer/legacy).
- Switches and APs are inventoried in [Devices.md](Devices.md); two USW Flex Mini
  switches currently report offline (BL-006).
