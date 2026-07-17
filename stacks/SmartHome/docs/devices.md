# Smart-home device inventory

Purchased smart-home end-devices. **Seeded 2026-07-17** from the UniFi client list
on the IoT (1040) + Consumer (1020) VLANs (network-visible devices) — annotate the
human bits (room, exact model, integration, purchase) as you go.

> Not exhaustive yet — only lists devices seen on the network. Battery/Zigbee/BLE
> devices that don't hold a Wi-Fi lease won't appear here until documented by hand.

## Devices

| Device | Type | Room | Integration | VLAN · IP | MAC | Model / notes | Purchased |
|--------|------|------|-------------|-----------|-----|---------------|-----------|
| Yeelight Lamp 4 | Light | _TODO_ | _TODO_ (Matter / Yeelight / MiHome?) | IoT · `10.40.18.24` | `58:b6:23:47:0e:79` | Xiaomi `yeelink-light-lamp4` | _TODO_ |
| Meross Smart Plug | Smart plug | _TODO_ | _TODO_ (Matter / Meross) | IoT · `10.40.170.241` | `48:e1:e9:dd:88:d9` | Meross | _TODO_ |
| Tuya device | _TODO_ (plug/bulb/sensor?) | _TODO_ | _TODO_ (Tuya / LocalTuya / Matter) | IoT · `10.40.169.147` | `c4:82:e1:6d:15:cc` | **⚠️ identify** — Tuya OUI | _TODO_ |
| Samsung Washer | Appliance | _TODO_ (Laundry?) | _TODO_ (SmartThings) | IoT · `10.40.84.50` | `50:fd:d5:85:6c:92` | Samsung | _TODO_ |
| Living Room TV | Cast target | Living Room | Chromecast → AirCast (CT 6002) | legacy · `192.168.179.132` | _(see aircast logs)_ | Chromecast — **consider moving to IoT 1040** | _TODO_ |
| _unidentified_ | _TODO_ | _TODO_ | _TODO_ | IoT · `10.40.2.131` | `d8:c8:0c:b0:9e:28` | **⚠️ identify** — unknown OUI | _TODO_ |
| Leapmotor C10 | EV (telemetry) | — | Leapmotor Mate (CT 4100) → MQTT → HA | _(cloud API, not on LAN)_ | — | VIN `LFZ93AN93SD112595` | _owned_ |
| **Arrowhead ESL-2** | Alarm panel | whole-house | **RE in progress** — keypad-bus → ESP32 → MQTT → HA ([docs →](devices/arrowhead-esl-2/)) | _not IP (serial/keypad bus)_ | — | ELITE-S; **discontinued/abandonware** | _owned_ |

## Per-device deep-dives

Devices with their own reverse-engineering / integration notes live under [`devices/`](devices/):

- [`arrowhead-esl-2/`](devices/arrowhead-esl-2/) — Arrowhead ESL-2 alarm panel: local HA integration via keypad-bus tap (ESP32), incl. the full [RS232 protocol reference](devices/arrowhead-esl-2/rs232-protocol.md).

## To identify / tidy
- **Tuya device** (`10.40.169.147`) — what is it? (name it in HA/UniFi.)
- **Unknown** (`10.40.2.131`, OUI `d8:c8:0c`) — identify + name.
- **Living Room TV** is on the **legacy** net, not IoT 1040 — AirCast finds it via mDNS reflection, but moving it onto IoT would tidy the segmentation.

## Notes
- **Integrations at a glance:** Matter devices → matter-server (CT 6001) → HA; MQTT
  devices → broker (CT 6000) → HA; Chromecast → AirCast (CT 6002); the C10 → Mate (CT 4100).
- The SmartHome **services** themselves (broker/matter/aircast/HA) live in
  [`../README.md`](../README.md), not here.
