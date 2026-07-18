# ESP board inventory — the DIY fleet

Bare ESP boards on hand for home-built devices, flashed + managed via the planned
**ESPHome LXC (CT 6003, [#251](https://github.com/Chrison-dev/Homelab/issues/251))**.
Seeded 2026-07-18.

## What's on hand

| Board | Qty | Chip | Core | Wi-Fi | BT / BLE | 802.15.4 (Zigbee/Thread) | Notes |
|-------|-----|------|------|:-----:|:--------:|:------------------------:|-------|
| ESP-12F | 1 | ESP8266 | Tensilica L106 | ✅ 2.4G b/g/n | ✖ | ✖ | Bare module; ~9 usable GPIO |
| ESP8266MOD | 3 | ESP8266 | Tensilica L106 | ✅ 2.4G | ✖ | ✖ | Generic modules (NodeMCU/Wemos-class) |
| ESP32-WROOM-32 | 1 | ESP32 | Xtensa dual | ✅ 2.4G | ✅ BT Classic + BLE 4.2 | ✖ | Workhorse: most GPIO, dual-core |
| ESP32-H2 Super Mini | 1 | ESP32-H2 | RISC-V | **✖ (no Wi-Fi)** | ✅ BLE 5 | ✅ | Radio board — Thread/Zigbee/Matter |
| XIAO ESP32-C6 | 2 | ESP32-C6 | RISC-V | ✅ **Wi-Fi 6** 2.4G | ✅ BLE 5 | ✅ | Tiny + most capable; all three radios |
| ESP32-H2-DEV-KIT-N4 | 1 | ESP32-H2 | RISC-V | **✖ (no Wi-Fi)** | ✅ BLE 5 | ✅ | Dev kit, 4 MB (N4), USB onboard |

## Which board for which job

- **Bluetooth proxy for HA** (the current blocker — HAOS has **no BT radio**, so BLE
  devices like the [Xiaomi thermometers](devices/xiaomi-lywsd03mmc/) can't be seen):
  needs **Wi-Fi + BLE** → **ESP32-WROOM-32** (proven, easy to dedicate) or a
  **XIAO ESP32-C6** (Wi-Fi 6 + BLE, tiny). ❌ *not* the ESP32-H2 (no Wi-Fi to reach HA),
  ❌ *not* ESP8266 (no BLE). **This is the recommended first fleet build.**
- **[ESL-2](devices/arrowhead-esl-2/) keypad-bus tap** (5 V CLK/DAT, needs level-shifting,
  timing-sensitive): **ESP32-WROOM-32** (ample GPIO, dual-core) or **XIAO ESP32-C6**.
- **Zigbee / Thread / Matter-over-Thread experiments**: the **ESP32-H2** pair + the
  **C6** pair (all have the 802.15.4 radio). Note the H2 boards are radio-only (no Wi-Fi).
- **Simple Wi-Fi sensors / switches** (temp-humidity, relays, buttons): the **ESP8266**
  boards (ESP-12F + 3× ESP8266MOD) — cheap, plenty for a one-sensor node.

## Caveats

- **ESP8266** = Wi-Fi only: no BLE, no 802.15.4, single-core, least RAM. Fine for basic
  ESPHome nodes, not for anything Bluetooth/Zigbee.
- **ESP32-H2** = **no Wi-Fi**. It cannot be a Wi-Fi-reachable ESPHome node or BT proxy;
  it's a Thread/Zigbee/BLE *radio* board (OpenThread/Zigbee/Matter, or USB).
- **C6 / H2** are newer chips — ESPHome/toolchain support is good but younger than ESP32/8266;
  expect the occasional rough edge.

## Related

- ESPHome LXC (dashboard/builder): [#251](https://github.com/Chrison-dev/Homelab/issues/251).
- BLE-proxy rationale + the Xiaomi sensor integration: [`devices/xiaomi-lywsd03mmc/`](devices/xiaomi-lywsd03mmc/).
- ESL-2 alarm bridge (an ESP32 project): [`devices/arrowhead-esl-2/`](devices/arrowhead-esl-2/).
</content>
