# Tube's ZB Gateway (EFR32 / MGM210, PoE)

A **[TubesZB](https://tubeszb.com) Zigbee coordinator** — a semi-custom, China-built
board (an **ESP32** host + a **Silicon Labs EFR32 MGM210** radio) that exposes the
Zigbee radio over the network. It's our whole-house Zigbee coordinator: sensors,
bulbs, plugs, etc. join its mesh, and it hands that mesh to Home Assistant.

## Identification (confirmed on the network)

| Field | Value |
|-------|-------|
| UniFi client name | `tube-zb-gw-efr32-c762b0` |
| MAC | `20:43:a8:c7:62:b3` — **OUI = Espressif (Shanghai, CN)** → the host is an **ESP32** |
| Model | **Chinese OEM clone** of the TubesZB `efr32-MGM210-poe` (EFR32 **MGM210** radio, PoE) — manual in Chinese, cites **gpio.club** / **zigbee.cc**. Not a genuine TubesZB. |
| Firmware | ESPHome-family, but an **OEM build with HTTP Basic auth** (hostname cloned from TubesZB `tube_zb_gw_efr32`; genuine TubesZB fw has no auth — see Credentials). |
| Radio stack | Silicon Labs **EZSP / EmberZNet** (Zigbee). The EFR32 can also run Thread — "Zigbee among others". |
| Link | **Ethernet / PoE** (no Wi-Fi) |
| Current IP | `192.168.179.222` (legacy net, DHCP) — **planned move → IoT VLAN 1040 + DHCP reservation** |

## Access

The ESP32 runs ESPHome; its only job is to bridge the EFR32's UART out over TCP so a
Zigbee stack (Zigbee2MQTT / ZHA) can drive the radio.

| What | Where |
|------|-------|
| Web UI (ESPHome) | `http://192.168.179.222/` — or mDNS `http://tube_zb_gw_efr32.local/` |
| Zigbee serial (for Z2M/ZHA) | `tcp://192.168.179.222:6638` (adapter: `ember`) |
| Firmware / OTA | via our ESPHome dashboard (CT 6003) once adopted; or the [TubesZB web flasher](https://tube0013.github.io/TubesZB-ESPHome-Builder/) |

### ⚠️ Credentials — this OEM unit has a web login (unlike genuine TubesZB)

> **Correction (2026-07-18):** genuine TubesZB ESPHome firmware ships with *no* auth,
> but **this unit is a Chinese OEM board, not a real TubesZB** (Chinese manual, refers
> to **gpio.club** + **zigbee.cc**), and it does **not** run stock TubesZB firmware.

The web UI (`http://192.168.179.222/`) is behind **HTTP Basic auth**
(`401 … Basic realm="Login Required"`). The factory default login is:

| | |
|---|---|
| **Username** | `cangji` |
| **Password** | `cangji` |

**Verified working 2026-07-18.** It's the vendor's documented default (brand **藏机 =
pinyin "cangji"**), from the tutorial on their file host [`zigbee.cc`](https://zigbee.cc)
→ *藏机多模版网关* (`网页已经加密-账号:cangji-密码:cangji`). ⚠️ **Still the factory
default — rotate it** as step 1 of hardening (see below); the credential lives in the
Bitwarden item and should be updated there when changed.

> **Bitwarden:** item **`smarthome · tube-zb-gw-efr32`** holds the URLs + this
> Basic-auth finding, and is where the recovered/rotated web login (and, if we later
> reflash our own ESPHome, the OTA password + API key) will live.

## Handbook / references

- **Official docs (handbook):** <https://tube0013.github.io/tube_gateways/>
  - EFR32 coordinators: <https://tube0013.github.io/tube_gateways/zigbee-coordinators/efr32-based/>
  - Getting started (Zigbee): <https://tube0013.github.io/tube_gateways/getting-started/zigbee/>
- **Docs + hardware repo:** <https://github.com/tube0013/tube_gateways>
  - Our model: [`models/current/tubeszb-efr32-MGM210-poe/`](https://github.com/tube0013/tube_gateways/tree/main/models/current/tubeszb-efr32-MGM210-poe)
  - Stock ESPHome firmware yaml: [`…/firmware/esphome/tubeszb-efr32-mgm210-poe-2023.yaml`](https://github.com/tube0013/tube_gateways/blob/main/models/current/tubeszb-efr32-MGM210-poe/firmware/esphome/tubeszb-efr32-mgm210-poe-2023.yaml)
- **Firmware builder / flasher:** <https://github.com/tube0013/TubesZB-ESPHome-Builder> · <https://tube0013.github.io/TubesZB-ESPHome-Builder/>
- **Store:** <https://tubeszb.com/>

## How it fits the SmartHome stack

```
Tube ZB GW (ESP32 + EFR32, PoE)
  ├─ EFR32 UART → :6638 ──socket──▶ Home Assistant ZHA (native)
  └─ ESP32 host firmware: vendor OEM build now → XZG planned (#259) — self-managed, NOT via our ESPHome dash
```

- The gateway is a **self-managed appliance** — it just exposes `socket://<ip>:6638`. It is
  **not** adopted by our ESPHome dashboard (CT 6003); that LXC is for our own DIY ESP
  fleet. Long-term the ESP32 host firmware moves to **XZG** (backlog **#259**).
- **Zigbee stack = HA's native ZHA** (decided 2026-07-18), pointed at `socket://<ip>:6638`,
  radio type EZSP/EmberZNet. The earlier standalone **Zigbee2MQTT (CT 6004) plan is dropped** —
  staying on ZHA avoids re-pairing, and the coordinator is a standalone box either way.
- The mesh rides the LAN socket, so it's unaffected by the HA VM→LXC cutover (#250).

## Migration & hardening plan

1. **Segment:** move the gateway to **IoT VLAN 1040** + a **DHCP reservation** so ZHA has a
   stable `socket://` target. (Re-point ZHA's serial path afterward.)
2. **Rotate the default login:** it still ships on `cangji`/`cangji` — change it and
   update the Bitwarden item. (On the **XZG** migration, #259, set our own auth instead.)
3. **Zigbee: staying on ZHA** — no lift-and-shift, no re-pairing. (ZHA→Z2M would have forced
   a full re-pair; decided against it 2026-07-18.)

## Firmware backup

A recovery baseline (vendor V2.5 ESP images + EFR32 NCP + the real PDF manual) is kept in
[`firmware-backup/`](firmware-backup/) with `SHA256SUMS`. See its README for provenance
(third-party host — unverified vendor binaries) and flashing notes.

## ⚠️ `Esp_Bluetooth` switch = BLE-gateway mode — it can take HA down

**Incident 2026-07-18:** the gateway's `switch.tube_zb_gw_efr32_c762b0_esp_bluetooth`
enables a **Passive BLE Monitor "BLE gateway"** that fires a `ble_monitor.parse_data`
Home-Assistant **service call for every BLE advertisement in range** (dozens/sec). When the
receiving side isn't perfectly set up, HA floods with errors, the recorder gets hammered, the
supervisor watchdog restarts the core, and the **UI goes unresponsive** — it looks exactly
like "HA crashed." Twice in a row it kept HAOS from finishing startup; HA reached `RUNNING`
within 30 s each time the switch was turned **off**.

Two failure modes seen:
1. **ESPHome gate off** → `Service call ble_monitor.parse_data … rejected; enable this
   functionality in the options flow` (fix: ESPHome device → *Allow the device to perform
   Home Assistant actions*).
2. **Even with the gate on** → `ServiceNotFound: ble_monitor.parse_data not found` — the
   **Passive BLE Monitor integration isn't actually configured/loaded** (files present in
   `custom_components/ble_monitor` ≠ a working integration).

**Current state: `Esp_Bluetooth` is OFF and should stay off.** The BLE-gateway hack is a
poor fit for a 2 GB HAOS box. Do BLE the clean way instead — a dedicated **ESP32
`bluetooth_proxy`** (see [`../../esp-fleet.md`](../../esp-fleet.md) and
[`../xiaomi-lywsd03mmc/`](../xiaomi-lywsd03mmc/)). Turn the switch off via the gateway web UI,
or: `curl -u cangji:cangji -X POST -H 'Content-Length: 0' http://192.168.179.222/switch/esp_bluetooth/turn_off`.
