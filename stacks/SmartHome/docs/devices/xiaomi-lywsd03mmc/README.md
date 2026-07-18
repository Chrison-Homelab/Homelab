# Xiaomi Mi Temperature & Humidity Monitor 2 (LYWSD03MMC) ×2

Two Xiaomi **LYWSD03MMC** BLE thermometer/hygrometers (Telink **TLSR8250** SoC, CR2032).
Owned 2026-07-18, not yet integrated.

## The actual problem: HA has no Bluetooth

These are **BLE-only, battery** devices — they don't join Wi-Fi and won't appear in the
UniFi client list. More importantly, **our Home Assistant (HAOS VM 2000) has no Bluetooth
radio** (no USB dongle passed to the VM). So HA cannot hear these sensors directly — the
whole task is *"get their BLE advertisements into HA."* Every option below is a combination
of **(1) sensor firmware** + **(2) a BLE relay** that HA can talk to.

## Part 1 — Sensor firmware

| | Stock (Xiaomi/Mijia) | **pvvx `ATC_MiThermometer`** (custom) |
|---|---|---|
| Integration | Xiaomi Home app / cloud, or encrypted BLE bind-key | Open BLE advertisements — pick **BTHome v2**, ATC, or custom |
| Local/no-cloud | ✖ awkward (bind-key/cloud) | ✅ fully local |
| Flashing | — | **OTA from Chrome** ([TelinkMiFlasher.html](https://pvvx.github.io/ATC_MiThermometer/TelinkMiFlasher.html), Web-Bluetooth) — no wires, ~2 min |
| HA discovery | via Xiaomi integration | **native `bthome`** auto-discovery |
| Battery/config | fixed | longer life, adjustable interval, on-screen custom data |

**Recommended: flash [pvvx](https://github.com/pvvx/ATC_MiThermometer) → BTHome v2.** It's
the de-facto standard, OTA-flashable from a Chrome browser **on a machine that has BT** (e.g.
the MacBook), and lands as a native, cloud-free HA device.

> ⚠️ **Check the hardware revision FIRST.** LYWSD03MMC **HW B1.6** (units made since
> ~2025.03) ships new Xiaomi firmware that is **incompatible with the custom firmware — it
> can't be flashed.** The flasher reads the HW version on connect. If ours are B1.6, the
> pvvx path is out and we fall back to the Hub-2/cloud route (Part 2, option C).

## Part 2 — The BLE relay (the missing receiver)

HA needs *something with a BT radio* to forward advertisements. Options, best-fit first:

**A. Dedicated ESP32 Bluetooth Proxy (ESPHome)** — *recommended, and on-theme.*
An ESP32 flashed with ESPHome [`bluetooth_proxy`](https://esphome.io/components/bluetooth_proxy.html)
relays **all** nearby BLE adverts to HA over the ESPHome API — no local BT on HA needed.
Generic (not Xiaomi-specific), rock-solid, and it's the **ideal first build for our own ESP
fleet** (#251, CT 6003 ESPHome dashboard). One proxy covers a whole area.

**B. The Cangji/Tube ZB gateway's built-in BLE-proxy mode** — *reuse hardware we own.*
The gateway's main vendor firmware is *"Zigbee coordinator + BLE proxy"* (the `Esp Bluetooth`
toggle). Enabling it makes the gateway double as a BT proxy. Caveat: this **conflicts with
the XZG migration (#259)** — XZG is Zigbee-focused and may not offer BLE-proxy — so only lean
on this if we stay on vendor firmware.

**C. Xiaomi Smart Home Hub 2 (already owned)** — *works, but not local-first.*
The Hub 2 has BT and relays Xiaomi BLE sensors, **but**: AlexxIT's local
[`XiaomiGateway3`](https://github.com/AlexxIT/XiaomiGateway3) integration **does not yet
support Hub 2's BLE** (only Gateway 2/3, Aqara E1), and the official
[`ha_xiaomi_home`](https://github.com/XiaoMi/ha_xiaomi_home) relays BLE sensors via **cloud**
(Xiaomi account; central-hub local mode is China-only). So this keeps the sensors on **stock
firmware + Xiaomi cloud** — the fallback if flashing is impossible (B1.6 hardware) or we
don't mind cloud. See [`../xiaomi-smart-home-hub-2/`](../xiaomi-smart-home-hub-2/).

## Recommendation

1. **Check HW revision** on the flasher.
2. If flashable → **pvvx + BTHome v2** on both sensors.
3. **BLE relay = a dedicated ESP32 `bluetooth_proxy`** (kicks off the ESP fleet, #251) — or
   temporarily the Cangji gateway's BLE-proxy mode to prove it out without buying anything.
4. If **B1.6/unflashable** → keep stock, integrate via the **Hub 2** (cloud), and treat local
   BLE as blocked until a proxy exists.

Net: the sensors are the easy part — the durable win is standing up a **Bluetooth proxy**,
which also unlocks every future BLE device (and is the reason HA "can't pair" anything today).
</content>
