# Xiaomi Smart Home Hub 2

A Xiaomi multi-protocol hub (**Zigbee 3.0 + Bluetooth BLE/Mesh + Wi-Fi + IR blaster**).
Owned; **not yet on the network / not yet integrated** (confirm its Wi-Fi presence in UniFi
and record the exact model — likely `DGNWG05LM`/`ZNDMWG04LM`-class; check the label).

## Why it's interesting here

- **BLE relay candidate** for our [Xiaomi thermometers](../xiaomi-lywsd03mmc/) — HA has no
  Bluetooth radio, and the Hub 2 does. See caveats below.
- **Alternative Zigbee coordinator** to the [Tube ZB gateway](../tube-zb-gw-efr32/) — though
  we've committed to the Tube gw + Zigbee2MQTT, so the Hub 2's Zigbee is redundant for now.
- **IR blaster** — could bring IR devices (AC, TV) into HA.

## Integration options (with the catches)

| Path | Local? | Notes |
|------|--------|-------|
| **AlexxIT [`XiaomiGateway3`](https://github.com/AlexxIT/XiaomiGateway3)** | ✅ LAN | Exposes Zigbee/BLE/Mesh locally — **but Hub 2's BLE is NOT yet supported** (only Gateway 2/3, Aqara E1). Track: PR #822 / forks. |
| **Official [`ha_xiaomi_home`](https://github.com/XiaoMi/ha_xiaomi_home)** | ⚠️ cloud | Works, but BLE/Zigbee sub-devices come via **Xiaomi cloud** (account required); LAN mode only covers IP devices, central-hub local mode is China-only. |

## Verdict

Good hardware, but **not the local-first BLE relay we'd want today** — AlexxIT can't do Hub 2
BLE yet, and the official route is cloud. For the thermometers, a dedicated **ESP32 ESPHome
`bluetooth_proxy`** stays the cleaner local answer. Keep the Hub 2 as:
- the **fallback** if the thermometers turn out to be unflashable (B1.6 hardware) → stock +
  Xiaomi cloud via the official integration, and/or
- a future **IR** bridge.

**TODO:** power it on, find it in UniFi (Wi-Fi client), DHCP-reserve, record the model + MAC.
</content>
