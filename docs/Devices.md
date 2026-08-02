# Devices

Physical inventory for all homelab devices.

**Last updated:** 2026-08-01 — Proxmox node hardware, NAS volumes and UniFi gear all
re-verified live on that date. Read-only discovery only: `dmidecode` / `lscpu` /
`lspci` / `pvesm` per node, and the UniFi controller API (`stat/device`) for network
gear. *(Note: the UniFi **MCP** server cannot resolve its host in this environment —
it needs `UNIFI_LOCAL_HOST` — so the network figures come from the controller API
directly via `X-API-KEY`, not from the MCP.)*

---

## Proxmox Hosts

Three nodes, all on **Proxmox VE 9.2.4**, all **2 × 8 GB = 16 GB** (≈15 GB usable),
all on gigabit with WoL armed. Synology NFS is mounted at the host level on every
node (see [NAS](#nas)).

> ⚠️ **The node names do not describe the hardware.** `hpe-01` is an HP EliteDesk
> **Mini**, not the decommissioned ProLiant DL360p Gen8 — that machine is *not* in
> the cluster. Verified 2026-08-01.

| | hpe-01 | nuc-01 | desktop-01 |
|---|---|---|---|
| **Model** | HP EliteDesk 800 G2 DM 35W | Intel NUC D34010WYK | Gigabyte **B450 GAMING X** (AM4) |
| **CPU** | i5-6500T, 4c/4t, 3.1 GHz max | i3-4010U, 2c/4t, 1.7 GHz | Ryzen 5 3600, 6c/12t, 4.2 GHz max |
| **RAM** | 2 × 8 GB DDR4-2133 — **slots full** | 2 × 8 GB DDR3-1600 — **slots full** | 2 × 8 GB DDR4 — **2 of 4 slots free** |
| **GPU** | Intel HD 530 (iGPU) | Haswell-ULT (iGPU) | **Quadro P400** + **RX 6600** |
| **PCIe card slot** | **none** ⚠️ | none (SFF NUC) | ✅ x16 in use, x8 free |
| **Boot** | UEFI | UEFI | **Legacy BIOS** |
| **Kernel** | `7.0.12-1-pve` | `7.0.12-1-pve` | `7.0.14-5-pve` |
| **Disk** | 480 GB SATA SSD (HP MK000480GWXFF) | 128 GB SATA SSD (Crucial M550) | **2 TB NVMe** (Samsung 970 EVO Plus) |
| **`local-lvm`** | 320 GB, 44% used | 54 GB, 45% used | 1.71 TB, 19% used |
| **`local`** | 94 GB, 22% used | 39 GB, 64% used | 94 GB, 64% used |
| **NIC** | Intel `e1000e` (I219) | Intel `e1000e` (I218) | Realtek `r8169` (RTL8111) |
| **MAC** | `c8:d3:ff:9d:da:02` | `b8:ae:ed:72:82:fe` | `18:c0:4d:de:9f:82` |
| **Mgmt IP** | `10.0.0.13` | `10.0.0.11` | `10.0.0.12` |
| **Mgmt DNS** | `hpe-01.homelab.chrison.internal` | `nuc-01.…` | `desktop-01.…` |
| **BIOS** | N21 v02.21 (**2016**) | WYLPT10H.86A.0030 (**2014**) | F62b (2021) |
| **Idle draw** | ~22 W | ~10 W | ~65 W |
| **Power role** ([#191](https://github.com/Chrison-Homelab/Homelab/issues/191)) | always-on sentinel | always-on sentinel | **on-demand (sleep target)** |

**Migrated off the legacy subnet on 2026-08-02** ([#37](https://github.com/Chrison-Homelab/Homelab/issues/37)):
all three now sit on the **Network Devices VLAN (1000)**, carried on a tagged
`vmbr0.1000` sub-interface. `vmbr0` itself is `inet manual`, so guests still using the
port's untagged native VLAN (the old arr fleet, CT 2005) are unaffected. Prefer the
DNS names over the addresses — they are UniFi local-DNS records, so a future
re-address needs no config change anywhere.

### Roles

- **hpe-01** — the workhorse. Carries almost everything: the media stack (Plex CT
  5008 + both *arr fleets), the SmartHome stack, the Media/SmartHome podman hosts,
  and the adopted Home Assistant VM 2000. **31 running LXCs + 1 VM.**
- **nuc-01** — ingress and management. **Pangolin** (CT 2013, the public reverse
  proxy), the cloudflared HA replica, and Proxmox Datacenter Manager. **5 running
  LXCs.** *(Traefik CT 2007 and Teleport, previously listed here, are both retired —
  Traefik's origin no longer exists and Teleport never went live.)*
- **desktop-01** — dev, gaming and CI. Forgejo + runners, the ERP stack, topaz, and
  five stopped VMs. **7 running LXCs.** It is the designated sleep node, so #191's
  hard blocker is moving its always-on services off it first.

### Notes worth knowing

- **hpe-01 has no PCIe expansion slot.** The `x1`/`x4` entries `dmidecode -t slot`
  reports are internal **M.2**; the Desktop Mini chassis takes no add-in card. This
  is why the Quadro P400 cannot be moved to the node Plex runs on
  ([#334](https://github.com/Chrison-Homelab/Homelab/issues/334)).
- **desktop-01's GPUs:** the **RX 6600** is passed through to the gaming VMs (1002
  `gaming-vm-01`, 1003 `bazzite`) via the `AMD_Radeon_RX6600` PVE mapping. The
  **Quadro P400** is mapped (`NVIDIA_Quadro_P400`) but **assigned to nothing** and
  still on `nouveau` — bought for Plex transcoding, stranded pending a 4th node
  ([#334](https://github.com/Chrison-Homelab/Homelab/issues/334)).
- **Transcoding capability differs sharply.** HD 530 (Skylake) does HEVC 10-bit
  decode only in *hybrid* mode and cannot encode it; the Haswell iGPU has no HEVC
  hardware at all; the P400 (Pascal) does full HEVC 10-bit decode *and* encode with
  no NVENC session cap. 228 of 264 4K streams in the Plex library are HEVC 10-bit.
- **desktop-01 RAM is under-clocked:** the modules are Kingston Fury `KF3200C16D4/8GX`
  (DDR4-**3200**) running at **2400 MT/s** — XMP/DOCP is not enabled in BIOS. Free
  performance if wanted. Two free DIMM slots for expansion
  ([#116](https://github.com/Chrison-Homelab/Homelab/issues/116)).
- **desktop-01 boots Legacy BIOS**, unlike the other two — relevant to the Secure
  Boot work on its gaming VMs ([#160](https://github.com/Chrison-Homelab/Homelab/issues/160)).
- **hpe-01 and nuc-01 BIOS are from 2016 and 2014** and neither auto-boots after AC
  loss — both hang awaiting console
  ([#237](https://github.com/Chrison-Homelab/Homelab/issues/237)).
- **desktop-01's VMs (all stopped):** 1001 `Plex-VM`, 1002 `gaming-vm-01`,
  1003 `bazzite`, 1100 `buildvm`, 9999 `proxmoxsharp-dev`.

**Wake-on-LAN:** all three have WoL armed (`ethtool ... wol g`) and persisted via the
`wol-arm.service` unit. Wake from an always-on node with
`src/Proxmox/wake-node.sh <node>` (MAC registry baked in). The Intel-NIC nodes keep
WoL on by `e1000e` default; desktop-01 needs the unit because `r8169` clears WoL each
boot. **Wake from full power-off (S5) is verified working on desktop-01.**

---

## NAS

### DS1813-01 — Synology DS1813
- **Role:** Shared NAS storage for all Proxmox nodes via NFS
- **IP:** `10.0.0.10` on VLAN 1000 · **DNS:** `nas.homelab.chrison.internal`
  (migrated 2026-08-02, #340. The DNS record is attached to the DHCP **reservation**,
  so name and lease move together — that is why the move needed zero DNS edits.)
- **Link:** 4-port **802.3ad LACP bond** on switch ports 17–20 (port 17 is the LAG master)

Single-disk volumes, **no redundancy** (see [ADR-0004](adr/ADR-0004-storage-architecture.md)).
NFS volumes are mounted on every node (`shared`). Figures verified 2026-08-01.

| Volume | Export | Total | Used | Content | Role |
|---|---|---|---|---|---|
| `ds1813-nfs-volume-1` | `/volume1/Volume-1` | ~1.8 TB | 14% | images, rootdir, backup, iso, vztmpl, snippets, import | general |
| `ds1813-nfs-volume-2` | `/volume2/Volume-2` | ~3.6 TB | 4% | images, rootdir, backup, iso, vztmpl, snippets, import | general (most free) |
| `ds1813-nfs-volume-3` | `/volume3/Volume-3` | ~5.4 TB | **74%** | images, rootdir, backup, iso, vztmpl, snippets, import | **LEGACY media** — see below |
| `ds1813-nfs-volume-4` | `/volume4/Volume-4` | ~7.4 TB | 12% | `import` only | **current media** |

**The two media volumes both matter, and volume-3 is the bigger one.** The media-stack
rebuild moved only ~20% of the library, so the old 5000-block fleet and its data are
still live:

| | volume-3 (legacy) | volume-4 (current) |
|---|---|---|
| `media/` | **3.5 TB** | 821 GB |
| `torrents/` | **3.2 TB** | 845 GB |
| qBittorrent torrents | **470** (CT 5007) | 46 (CT 5104) |

Consequences to be aware of:

- **Plex depends on BOTH.** Every library spans them, and the **Animes** library exists
  *only* on volume-3. Both are declared in `stacks/Media/plex.lxc.yaml` as of
  [#329](https://github.com/Chrison-Homelab/Homelab/issues/329) — before that, the
  volume-3 mount existed only as a hand-written `fstab` line inside CT 5008 and a
  rebuild would have silently lost 81% of the library.
- **volume-3 is at 74%** and is the slower of the two (~425 Mbps vs ~731 Mbps measured
  sequential read from a CT), partly because the old qBittorrent seeds 470 torrents off
  the same spindles.
- Retiring volume-3 needs a decision about that 3.2 TB and 470 torrents, not just a
  container cutover ([#199](https://github.com/Chrison-Homelab/Homelab/issues/199)).

---

## Network Equipment

Managed by the UniFi Cloud Gateway (gateway MAC `1c:6a:1b:43:62:57`). Switches and
APs live on the **Network Devices** VLAN (10.0.0.0/16). Verified 2026-08-01.

| Device | Type | Model | Firmware | IP | Status |
|---|---|---|---|---|---|
| **Cloud Gateway Ultra** | `udm` | **UDRULT** | 5.1.19.33549 | `118.67.199.127` (WAN) | 🟢 20d |
| US 24 PoE 250W | `usw` | US24P250 | 7.4.1.16850 | 10.0.53.142 | 🟢 20d |
| AC LR (Lounge) | `uap` | U7LR | 6.8.2.15592 | 10.0.14.89 | 🟢 20d |
| AC LR (Kitchen) | `uap` | U7LR | 6.8.2.15592 | 10.0.93.133 | 🟢 20d |
| AC LR (Master Bedroom) | `uap` | U7LR | 6.8.2.15592 | 10.0.161.161 | 🟢 20d |
| USW Flex Mini (Lounge) | `usw` | USMINI | 2.1.6.762 | 10.0.217.66 | 🔴 **Offline** |
| USW Flex Mini (Master Bedroom) | `usw` | USMINI | 2.1.6.762 | 10.0.111.213 | 🔴 **Offline** |

The gateway is a **Cloud Gateway Ultra (UDRULT)** — previously recorded as "model
unknown, re-verify when convenient" because it isn't returned under the `ugw` device
type; it reports as `udm`. Its `ip` is the WAN address, so the LAN mgmt address is
still `192.168.178.1`. That WAN IP is also the target of the `*.lab` / `*.arr`
wildcard A records for Pangolin ingress (ADR-0007).

> **Both USW Flex Minis are offline** — physical triage needed
> ([#307](https://github.com/Chrison-Homelab/Homelab/issues/307), was BL-006).

### WiFi — three U7LR APs, 2.4 + 5 GHz (no 6 GHz on this model)

Radios are on **`channel: auto`**, which has produced a co-channel collision:

| AP | 2.4 GHz | 5 GHz | 2.4 util |
|---|---|---|---|
| Lounge | ch6 / 20 MHz | ch60 / 80 MHz | 32% |
| Kitchen | ch11 / 20 MHz | **ch36 / 80 MHz** | 37% |
| Master Bedroom | **ch6** / 20 MHz | **ch36 / 80 MHz** | 47% |

Kitchen and Master Bedroom share the same 80 MHz block (36–48), and Lounge and Master
Bedroom share 2.4 GHz ch6 while ch1 sits unused. `min_rssi` is disabled and 802.11r is
off on all WLANs, so distant clients never get nudged to roam. Diagnosis and the
proposed channel plan are in
[#331](https://github.com/Chrison-Homelab/Homelab/issues/331). Regulatory domain is
**NZ (country 554)**, which permits 5150–5350, 5470–5725 and 5725–5875.

SSIDs: `Blackbox` (both bands), `Blackbox_IOT` (2.4 GHz only), `89D Tao-Simon` (both).

---

## Other Devices

### Zigbee Gateway — TubesZB efr32 (MGM210 PoE)
- **Hostname:** `tube-zb-gw-efr32-c762b0` · **IP:** 192.168.179.222 (legacy) · **MAC:** `20:43:a8:c7:62:b3`
- **Role:** Whole-house Zigbee coordinator (feeds Home Assistant)
- **Status:** 🟢 reachable, web UI returns 401 (credentials in Bitwarden)
- **Note:** this is an **OEM "Cangji" clone** of the TubesZB `efr32-MGM210-poe`
  design, not a genuine TubesZB unit. Stock firmware is backed up; reflashing to
  **XZG** is tracked in [#259](https://github.com/Chrison-Homelab/Homelab/issues/259).

**It is two chips, and that matters for #259 and #336.** The Zigbee radio is a
Silicon Labs **EFR32 / MGM210**; the network side is an **ESP32 running ESPHome**
acting as a serial↔TCP bridge. Confirmed by the open ports and by the entities it
publishes to Home Assistant (`switch.…_esp_restart`, `sensor.…_esp_uptime`):

| port | what |
|---|---|
| 80 | web UI (401 — credentials in Bitwarden) |
| 6053 | **ESPHome native API** — this is the ESP32 half |
| 6638 | Zigbee serial-over-TCP — what ZHA actually connects to |

- **Power/switching:** PoE, ~2 W, on **`US 24 PoE 250W` port 21** ("ZigBee Adapter").
  The port carries an explicit override pinning it to the **Old Network**, so a VLAN
  move is a switch-port change, not just a DHCP edit. **No DHCP reservation exists.**
- **Home Assistant attaches TWICE:** the **ESPHome** integration (device entities) and
  **ZHA**, whose coordinator path is `socket://192.168.179.222:6638`. Both are
  address-bound today, so both must be updated if the address changes. Zigbee devices
  themselves do **not** need re-pairing — the network lives in the coordinator's NVRAM,
  which the move does not touch.

---

*Service inventory (LXCs/VMs) → [Services.md](Services.md). Network layout →
[Network.md](Network.md).*
