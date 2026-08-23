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
| **Old Network** (legacy, retiring) | untagged | 192.168.178.1/23 | localdomain | 192.168.178.11 – 192.168.179.254 |
| **One-Click VPN** | — | 192.168.9.1/24 | — | 192.168.9.6 – 192.168.9.254 |

### Network Devices — `10.0.0.0/16` (VLAN 1000)
Infrastructure and storage: switches, APs, **the NAS** (`10.0.0.10`) and **all three
Proxmox nodes** (`10.0.0.11` nuc-01 · `10.0.0.12` desktop-01 · `10.0.0.13` hpe-01),
migrated 2026-08-02 (#37).

Nodes and NAS share this VLAN deliberately: every NFS mount then stays inside one
firewall zone, so the management plane can be isolated later without breaking
storage. Nodes carry the address on a **tagged `vmbr0.1000`** sub-interface — `vmbr0`
itself is `inet manual`, which is what lets guests on the port's untagged native VLAN
keep reaching the Old Network while it is drained.

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

## Monitoring

UniFi gear is collected by **unpoller** on the monitoring podman host (CT 4001) and scraped
by Prometheus as job `unifi`. Grafana dashboard: **UniFi Network** (`unifi-network`, Homelab
folder).

Pulse does *not* cover UniFi — it monitors Proxmox, Docker, Kubernetes, TrueNAS and vSphere —
so the network gear lives in Prometheus/Grafana alongside the Synology SNMP job.

### How it authenticates

unpoller polls the controller's **classic** API (`/proxy/network/api/s/default/...`) with
`X-API-KEY`, using the same `UNIFI_API_KEY` the `*Sharp` CLIs use — so there is no second
credential to mint and no local UniFi admin account to create. It only ever issues GETs.

The controller is addressed as `unifi.homelab.chrison.internal`, which resolves to
**10.10.0.1** — the gateway's address on the Homelab VLAN (1010), the same VLAN as CT 4001.
Polling therefore stays on-VLAN and never crosses into the legacy `192.168.178.0/23` range
that `UNIFI_LOCAL_HOST` still uses. (That legacy address is kept deliberately for the *write*
path, which has to work while you are fixing a controller that is already broken.)

### What it covers

Per-device CPU / memory / temperature / uptime, per-switch-port PoE draw, per-radio channel
utilization and client counts, per-SSID average client signal, WAN throughput and link speed,
and site-level latency, client count and internet drops.

### Alerting

Seven rules in the `UniFi` group (Grafana unified alerting, provisioned under
`assets/grafana/provisioning/alerting/`), delivered to **Home Assistant** via a webhook
contact point → the HA automation *"Grafana alerts → notification"*.

| Rule | Fires when | Severity |
|---|---|---|
| UniFi collector is down | `up{job="unifi"} < 1` for 10m | critical |
| UniFi device dropped off the controller | a device present 1h ago is gone for 15m | critical |
| UniFi reports disconnected devices | `unpoller_site_disconnected > 0` for 15m | warning |
| Wi-Fi channel saturated | radio airtime > 70% for 20m | warning |
| PoE budget under pressure | switch PoE draw > 180 W for 15m | warning |
| WAN latency is high | WWW latency > 150 ms for 15m | warning |
| UniFi device running hot | device temperature > 80 °C for 15m | warning |

Two things worth knowing about these:

- **An offline device emits no metrics at all**, rather than a zero — unpoller only reports
  what the controller currently sees. That is why the "dropped off" rule compares against
  `offset 1h` instead of testing a value. It catches gear that fails *from now on*; anything
  already offline before the rule existed (the two USW Flex Minis, [#307]) appears on neither
  side of the comparison and will never fire. The site-level `disconnected` rule is the
  complement for adopted-but-unreachable gear.
- **Thresholds are set against this network's measured baseline**, not vendor defaults. WWW
  latency sits near 2 ms, 2.4 GHz airtime runs 0.2–0.5, and PoE draw is well under 20 W of the
  switch's 250 W budget. The channel-utilization rule is deliberately above today's worst
  reading, so fixing the channel plan ([#331]) is the remedy rather than muting the alert.

`noDataState` is `OK` on every rule except the collector one: absent series are normal here
(a model that reports no temperature, no device having vanished), and treating absence as
failure would make the whole set cry wolf. If unpoller stops being scraped, though, every
other rule goes blind — so that one is `Alerting`.

[#307]: https://github.com/Chrison-Homelab/Homelab/issues/307
[#331]: https://github.com/Chrison-Homelab/Homelab/issues/331
