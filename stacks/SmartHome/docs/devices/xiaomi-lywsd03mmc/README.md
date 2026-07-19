# Xiaomi Mi Temperature & Humidity Monitor 2 (LYWSD03MMC) ×2

Two Xiaomi **LYWSD03MMC** BLE thermometer/hygrometers (Telink **TLSR8250** SoC, CR2032).
Owned 2026-07-18; **flashed + integrated 2026-07-20** — see Outcome below.

## ✅ Outcome (done 2026-07-20)

Both units **flashed to pvvx `ATC_v58` → BTHome v2 (unencrypted)** and are live in Home Assistant.

| Unit | HW rev | Stock fw | BLE MAC | pvvx name |
|---|---|---|---|---|
| #1 | **B1.4** | 2.1.1_0159 | `A4:C1:38:1F:09:C0` | `ATC_1F09C0` |
| #2 | **B1.4** | 2.1.1_0159 | `A4:C1:38:20:6E:6B` | `ATC_206E6B` |

- **HW was B1.4, not B1.6** — the "unflashable hardware" fear (Part 1 caveat) didn't apply; both
  flashed cleanly. OTA from **Chrome** (Web Bluetooth) on the MacBook.
- **BLE relay = the Tube/Cangji gateway's `Esp_Bluetooth` BLE-proxy mode** (Part 2 · option B),
  re-enabled and currently serving both sensors into HA. ⚠️ This mode previously flooded HA
  offline twice ([details](../tube-zb-gw-efr32/#-esp_bluetooth-switch--ble-gateway-mode--it-can-take-ha-down))
  — **working as of 2026-07-20, but monitor it**; if it destabilises, fall back to a dedicated
  ESP32 `bluetooth_proxy` (option A, #251), which remains the more robust long-term path.
- Mi Home cloud tokens/bind-keys for both units are saved in **Bitwarden** (item *"Xiaomi Mi Home
  — device tokens & bind keys (homelab)"*) for a possible stock-firmware restore; unused on custom fw.

### Gotchas the original plan didn't foresee (newer firmware)
- **Stock fw `2.1.1_0159` blocks the direct OTA flash.** The flasher must first **Login with the
  device's Mi bind-key + token + `did`**, obtained via a one-time **Mi Home registration** +
  [`Xiaomi-cloud-tokens-extractor`](https://github.com/PiotrMachowski/Xiaomi-cloud-tokens-extractor).
  Only then does the custom flash authorize. (The classic "just connect and flash" path is for
  older firmware.) You do **not** need the original Mi account — bind it fresh to any account.
- **Reset ≠ battery pull.** The LYWSD03MMC has no button; Mi Home's *"please confirm the device has
  been reset"* is cleared by **shorting the RESET + GND pads for ~7 s** (screen restarts), *then*
  Mi Home will re-add it.
- The B1.5/B1.6 *"flash `Original_OTA` first"* intermediate step is **only** for those revisions —
  **B1.4 goes straight to `ATC_vNN`** after Login.

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
>
> ✅ **Resolved:** both our units read **HW B1.4** on connect — flashed cleanly (see Outcome).
> Their stock fw (`2.1.1_0159`) still needed the Mi bind-key Login step, but not the B1.6 block.

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

## Recommendation → what we did

1. ~~Check HW revision~~ → **B1.4** (flashable). ✅
2. ~~If flashable → pvvx + BTHome v2~~ → **done, both sensors** (`ATC_v58`, unencrypted). ✅
3. **BLE relay** → we took the *"prove it out without buying anything"* route: the **Cangji
   gateway's BLE-proxy mode** (option B). Working. A dedicated ESP32 `bluetooth_proxy` (option A,
   #251) is still the recommended durable relay — switch to it if the gateway proxy misbehaves.
4. *(B1.6/unflashable fallback never needed.)*

Net: the sensors were the easy part. The durable win — a robust **Bluetooth proxy** that unlocks
every future BLE device — is *interim-solved* via the gateway; the dedicated ESP32 proxy (#251)
is the graduation path.
</content>
