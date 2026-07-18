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
| Alen BreatheSmart 45i | Air purifier | _TODO_ | _TODO_ (Tuya / LocalTuya?) | IoT · `10.40.169.147` | `c4:82:e1:6d:15:cc` | Alen 45i True HEPA (UniFi-named; was mis-tagged "Tuya device") | _TODO_ |
| Samsung Washer | Appliance | _TODO_ (Laundry?) | _TODO_ (SmartThings) | IoT · `10.40.84.50` | `50:fd:d5:85:6c:92` | Samsung | _TODO_ |
| Living Room TV | Cast target | Living Room | Chromecast → AirCast (CT 6002) | legacy · `192.168.179.132` | _(see aircast logs)_ | Chromecast — **consider moving to IoT 1040** | _TODO_ |
| Nest Protect | Smoke/CO alarm | _TODO_ | _TODO_ (Nest / Google) | IoT · `10.40.2.131` | `d8:c8:0c:b0:9e:28` | Nest Protect (UniFi-named) | _TODO_ |
| **Tube's ZB Gateway** | Zigbee coordinator | whole-house | ESPHome dash (CT 6003, planned) + Zigbee2MQTT (CT 6004, planned) → MQTT → HA ([docs →](devices/tube-zb-gw-efr32/)) | legacy · `192.168.179.222` → **move to IoT 1040** | `20:43:a8:c7:62:b3` | **Chinese OEM "藏机/Cangji"** multi-mode gw (TubesZB `efr32-MGM210-poe` clone); ESP32 + EFR32; web UI login **`cangji`/`cangji`** (default — rotate; in BW) | _owned_ |
| Leapmotor C10 | EV (telemetry) | — | Leapmotor Mate (CT 4100) → MQTT → HA | _(cloud API, not on LAN)_ | — | VIN `LFZ93AN93SD112595` | _owned_ |
| **Arrowhead ESL-2** | Alarm panel | whole-house | **RE in progress** — keypad-bus → ESP32 → MQTT → HA ([docs →](devices/arrowhead-esl-2/)) | _not IP (serial/keypad bus)_ | — | ELITE-S; **discontinued/abandonware** | _owned_ |

## Per-device deep-dives

Devices with their own reverse-engineering / integration notes live under [`devices/`](devices/):

- [`arrowhead-esl-2/`](devices/arrowhead-esl-2/) — Arrowhead ESL-2 alarm panel: local HA integration via keypad-bus tap (ESP32), incl. the full [RS232 protocol reference](devices/arrowhead-esl-2/rs232-protocol.md).
- [`tube-zb-gw-efr32/`](devices/tube-zb-gw-efr32/) — TubesZB Zigbee coordinator (ESP32 + EFR32, PoE): access/handbook, the stock-firmware **no-auth** finding, and the ESPHome + Zigbee2MQTT integration/hardening plan.

## To identify / tidy
- **Tube's ZB Gateway** (`192.168.179.222`) — on the **legacy** net; move to **IoT 1040** + DHCP-reserve before Zigbee2MQTT points at it (see [deep-dive](devices/tube-zb-gw-efr32/)).
- **Living Room TV** is on the **legacy** net, not IoT 1040 — AirCast finds it via mDNS reflection, but moving it onto IoT would tidy the segmentation.
- ~~**Tuya device** (`10.40.169.147`)~~ → identified as **Alen BreatheSmart 45i** air purifier (2026-07-18).
- ~~**Unknown** (`10.40.2.131`, OUI `d8:c8:0c`)~~ → identified as **Nest Protect** (2026-07-18).

## Notes
- **Integrations at a glance:** Matter devices → matter-server (CT 6001) → HA; MQTT
  devices → broker (CT 6000) → HA; Chromecast → AirCast (CT 6002); the C10 → Mate (CT 4100).
- The SmartHome **services** themselves (broker/matter/aircast/HA) live in
  [`../README.md`](../README.md), not here.
