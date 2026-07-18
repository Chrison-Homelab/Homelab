# Firmware backup — 藏机/Cangji multi-mode gateway (TubesZB efr32-MGM210-poe clone)

Recovery baseline for the [Tube's ZB Gateway](../README.md). Two independent chips:
the **ESP32 host** (ethernet + web UI + serial-over-TCP bridge) and the **EFR32 MGM210**
Zigbee radio (its own EmberZNet "NCP" firmware). They flash separately.

> ⚠️ **Provenance / trust:** these were downloaded from the vendor's own file host
> **`https://zigbee.cc`** (an [AList](https://alist.nn.ci) browser) → *藏机多模版网关*,
> **2026-07-18**. It's a third-party Chinese host — treat as *unverified vendor binaries*.
> They're kept as a **recovery baseline** (what shipped on the device), not a trusted
> upstream. See `SHA256SUMS` for integrity; re-verify before flashing. Prefer flashing
> a **known-open-source** firmware (genuine TubesZB / XZG / our own ESPHome) long-term.

## Files

| File | Chip | What |
|------|------|------|
| `esphome-tube-zb-gw-efr32-V2.5.bin` | ESP32 | **Main OTA image** — Zigbee coordinator + BLE proxy + BLE gateway (BLE normally-off) |
| `esphome-tube-zb-gw-efr32-zigbee-V2.5.bin` | ESP32 | OTA image — Zigbee coordinator **only** |
| `esphome-tube-zb-gw-efr32-router-V2.5.bin` | ESP32 | OTA image — Zigbee **router** mode |
| `esphome-tube-zb-gw-efr32.factory-V2.5.bin` | ESP32 | **Factory image** — full flash via **serial** (recovery if OTA bricks) |
| `ncp-uart-sw_7.4.5.0_115200.ota` | EFR32 | Zigbee **NCP** coordinator firmware (EmberZNet 7.4.5, 115200 baud) |
| `vendor-manual-多模板图文教程.pdf` | — | Vendor illustrated manual (the real handbook; paper copy had no creds) |
| `vendor-tutorial-text.ini` | — | Vendor text tutorial — **documents the `cangji`/`cangji` default login** + Z2M/ZHA setup |
| `esphome-image-variants.ini` | — | Vendor note explaining the ESP32 image variants above |

## Flashing notes (from the vendor tutorial)

- ESP32 images are **OTA-flashable** from the web UI: *ESPHome device → OTA Update →
  select `.bin` → Update*. Serial recovery uses the `.factory` image.
- The EFR32 NCP (`ncp-uart-*_115200.ota`) is flashed **through** the ESP: either the
  vendor's zigbee-uploader ESP image, or from Zigbee2MQTT (`ember` adapter has an OTA
  firmware-update path). Flashing the NCP does **not** wipe the Zigbee network by itself,
  but **take a coordinator backup first**.
- Full versioned tree (V2.0–V2.5, router NCPs, tasmota "enterprise" images) lives on
  `zigbee.cc` if an older/other build is ever needed.
</content>
