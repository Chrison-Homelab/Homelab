# Smart-home device inventory

Smart-home end-devices. **Seeded 2026-07-17** from UniFi (network-visible clients) and
**enriched 2026-07-18** from the Home Assistant device registry (which also surfaces
Zigbee / cloud / IR devices that never hold a Wi-Fi lease). Annotate the human bits
(exact room, purchase) as you go.

> Sources: UniFi = Wi-Fi/wired clients (gives VLAN·IP·MAC). HA = manufacturer/model/room
> + integration. Battery-BLE devices (e.g. the Xiaomi thermometers) appear in neither until
> a BLE proxy exists. IPs for Wi-Fi Tuya/Tapo devices are `_TODO_` (find in UniFi).

> ⚠️ **Post-move (2026):** the HA config carried over from the **previous rental**, so its
> device registry contains **ghosts** — devices that didn't make the move or aren't set up
> yet in the new home. **Rule of thumb:** a device with a *current UniFi lease* is confirmed
> live; an **HA-only** entry (no lease, and not Zigbee/BLE/IR) may be **stale — verify
> physically.** Rows below tagged _verify_ are HA-only and unconfirmed in the new home.

## Devices

| Device | Type | Room | Integration | VLAN · IP | MAC | Model / notes | Purchased |
|--------|------|------|-------------|-----------|-----|---------------|-----------|
| Yeelight lights | Lights | _TODO_ | Yeelight / `xiaomi_home` | IoT (Lamp4 = `10.40.18.24`) | `58:b6:23:47:0e:79` (Lamp4) | ⚠️ **verify** — HA lists 7 (`lamp4`/`color3`/`color5`/`mono6`/`monoa`) but **most didn't survive the move**; confirm which are actually alive | _owned (partial)_ |
| AC — Master Bedroom (upstairs) | Air conditioner | Master Bedroom | planned — **wired CNS** (MHI-AC-Ctrl-ESPHome) *or* IR fallback | _(CNS serial / or IR)_ | — | **Mitsubishi Heavy Industries (MHI) SRK-ZSA "Bronte"** R32 inverter — *not* Mitsubishi Electric (remote **RLA502A720** → SRK20/25/35/50ZSA-W family; exact kW **TBC** from the outdoor `SRC…` nameplate). Wired route = CNS/SPI, not Daikin S21 or Mits-Electric CN105. **Not set up yet** | _owned_ |
| AC — Downstairs | Air conditioner | Downstairs | **planned — S21 wired** (XIAO ESP32-C6 + ESPHome) ([docs →](devices/daikin-ftxf-s21/)) | IoT 1040 · `_TODO_` (C6, Wi-Fi) | — | **Daikin FTXF50TVMA** (Cora, 5.0 kW; ser. `E009281`). **Not IR** — taps the S21 port for local bidirectional control | _owned_ |
| Sony Bravia KD-49XF7596 | TV / cast | Lounge | `braviatv` + Cast (→ AirCast CT 6002) | legacy · `192.168.179.132` | `d8:9c:67:cb:42:eb` | Sony Android TV (Chromecast built-in); one device, multiple integrations — **move to IoT 1040** | _owned_ |
| Philips Android TV | TV / cast | Master Bedroom | Android TV / Cast | legacy · `192.168.178.208` | `84:3e:1d:6c:bf:a2` | **TPV / TP Vision** "2020 FHD Android TV" (= Philips) — OUI Gaoshengda ODM | _owned_ |
| Apple TV 4K (gen 3) | Media / HomeKit-Matter hub | Master Bedroom | `apple_tv` | legacy · `192.168.178.71` | `c4:f7:c1:51:61:76` | Apple | _owned_ |
| Echo · Lounge | Voice assistant | Lounge | `alexa_media` | legacy · `192.168.178.200` | `e8:4c:4a:40:0c:52` | Amazon Echo | _owned_ |
| Echo · Bedroom | Voice assistant | Bedroom | `alexa_media` | legacy · `192.168.178.145` | `10:ce:02:d0:d3:99` | Amazon Echo | _owned_ |
| Broadlink RM4 mini | IR blaster | _TODO_ | `broadlink` (+ SmartIR) | _TODO_ | — | ⚠️ **not installed yet** — needed to control both ACs; the old Haier AC (`climate.lounge_ac`) was the **previous rental** and is gone | _owned_ |
| Heater | Smart heater | Karls Bedroom | Tuya (`localtuya`/`tuya`) | IoT · `_TODO_` | — | Tuya `PEH224/225HA` — ⚠️ _verify_ (HA-only, no current lease) | _owned_ |
| Tower Fan | Fan | Karls Bedroom | Tuya | IoT · `_TODO_` | — | Goldair Platinum Tower Fan — ⚠️ _verify_ | _owned_ |
| Standing Fan | Fan | Office | Tuya | IoT · `_TODO_` | — | Tuya (model TBC) — ⚠️ _verify_ | _owned_ |
| Aromalife | Aroma diffuser | Karls Bedroom | Tuya | IoT · `_TODO_` | — | Tuya "Aromalife" — ⚠️ _verify_ | _owned_ |
| Alen BreatheSmart 45i | Air purifier | _TODO_ | _TODO_ (Tuya?) | IoT · `10.40.169.147` | `c4:82:e1:6d:15:cc` | Alen 45i True HEPA (live on network) | _owned_ |
| Tapo C200 | Wi-Fi camera | Karls Bedroom | `tapo` (TP-Link) | IoT · `_TODO_` | — | TP-Link Tapo C200 (motion/person/baby-cry) — ⚠️ _verify_ | _owned_ |
| Environment Sensor T1 | **Zigbee** temp/humidity | Karls Bedroom | **ZHA** (via Tube gw) | _(Zigbee, not IP)_ | — | Tuya `TS0601` (`_TZE200_a8sdabtg`) | _owned_ |
| Nest Protect | Smoke/CO alarm | _TODO_ | _TODO_ (Nest) | IoT · `10.40.2.131` | `d8:c8:0c:b0:9e:28` | Nest Protect | _owned_ |
| Meross Smart Plug | Smart plug | Garage | `meross_lan` (local) | IoT · `10.40.170.241` | `48:e1:e9:dd:88:d9` | Meross `mss310` | _owned_ |
| Samsung Washer | Appliance | Laundry | SmartThings | IoT · `10.40.84.50` | `50:fd:d5:85:6c:92` | Samsung `DA_WM_TP2_20` | _owned_ |
| Xiaomi Temp & Humidity Monitor 2 ×2 | BLE temp/humidity | _TODO_ | **planned** — pvvx/BTHome + BLE proxy ([options →](devices/xiaomi-lywsd03mmc/)) | _not IP (BLE, battery)_ | — | `LYWSD03MMC`; blocked — **HA has no BT**; check HW rev (B1.6 = unflashable) | _owned (2026-07-18)_ |
| **Tube's ZB Gateway** | Zigbee coordinator (+ BLE gw, disabled) | Garage | **ZHA** today (`socket://…:6638`); → Z2M planned ([docs →](devices/tube-zb-gw-efr32/)) | legacy · `192.168.179.222` → **move to IoT 1040** | `20:43:a8:c7:62:b3` | OEM "藏机/Cangji" TubesZB `efr32-MGM210-poe` clone; web login `cangji`/`cangji` (in BW); ⚠️ `Esp_Bluetooth` off | _owned_ |
| Xiaomi Smart Home Hub 2 | Multi-protocol hub | _TODO_ | _TODO_ — Zigbee/BLE/IR ([notes →](devices/xiaomi-smart-home-hub-2/)) | **⚠️ detect** (Wi-Fi) | _TODO_ | Alt Zigbee/BLE hub; not the local-first BLE relay | _owned_ |
| Leapmotor C10 | EV (telemetry) | Driveway | Leapmotor Mate (CT 4100) → MQTT → HA | _(cloud API)_ | — | VIN `LFZ93AN93SD112595`; incl. Digital Key | _owned_ |
| **Arrowhead ESL-2** | Alarm panel | whole-house | **RE in progress** — keypad-bus → ESP32 → MQTT → HA ([docs →](devices/arrowhead-esl-2/)) | _not IP (serial/keypad bus)_ | — | ELITE-S; **discontinued/abandonware** | _owned_ |

> **Not smart-home devices** (seen in HA/UniFi but infra): the "AC LR (…)" entries are
> **UniFi U7LR access points** (not air-con), "USW Flex Mini" are UniFi switches, plus the
> Synology NAS, Proxmox nodes, and weather/rubbish-collection service integrations.

## Zigbee — staying on ZHA (decided 2026-07-18)

The Zigbee mesh runs on **ZHA** (native HA) against the standalone Tube gateway coordinator
(`socket://192.168.179.222:6638`), and **we're keeping it that way** — no Zigbee2MQTT migration
(it'd force re-pairing every device for no real gain; the coordinator is a standalone box either
way). **→ The planned Zigbee2MQTT LXC (CT 6004) is dropped.** The Tube gateway stays as the ZHA
coordinator; the XZG firmware move (#259) is still compatible (same socket). ESPHome LXC (6003,
#251) is unaffected — it's for the DIY ESP fleet.

## Per-device deep-dives

Devices with their own reverse-engineering / integration notes live under [`devices/`](devices/):

- [`daikin-ftxf-s21/`](devices/daikin-ftxf-s21/) — Downstairs Daikin FTXF50 (Cora): local, bidirectional HA climate via the **S21 port** (XIAO ESP32-C6 + ESPHome) instead of IR — incl. wiring, [BOM + level-shifter](devices/daikin-ftxf-s21/hardware/) and a ready [ESPHome config](devices/daikin-ftxf-s21/esphome-daikin-downstairs.yaml.example).
- [`arrowhead-esl-2/`](devices/arrowhead-esl-2/) — Arrowhead ESL-2 alarm panel: local HA integration via keypad-bus tap (ESP32), incl. the full [RS232 protocol reference](devices/arrowhead-esl-2/rs232-protocol.md).
- [`tube-zb-gw-efr32/`](devices/tube-zb-gw-efr32/) — TubesZB Zigbee coordinator (ESP32 + EFR32, PoE): access/handbook, the OEM-clone finding, the `Esp_Bluetooth` HA-flood incident, and the Z2M/XZG plans.
- [`xiaomi-lywsd03mmc/`](devices/xiaomi-lywsd03mmc/) — Xiaomi LYWSD03MMC BLE thermometers: the "HA has no Bluetooth" root cause, pvvx/BTHome flashing, HW-B1.6 caveat, and BLE-proxy relay options.
- [`xiaomi-smart-home-hub-2/`](devices/xiaomi-smart-home-hub-2/) — Xiaomi Hub 2 (Zigbee/BLE/IR): integration options + why it isn't the local-first BLE relay yet.

## To identify / tidy
- **Wi-Fi IPs** for the Tuya/Tapo/Broadlink devices — capture from UniFi + DHCP-reserve.
- **Tube's ZB Gateway** (`192.168.179.222`) — on the **legacy** net; move to **IoT 1040** + DHCP-reserve before any Z2M cutover (see [deep-dive](devices/tube-zb-gw-efr32/)).
- **Consumer/media devices on the legacy net** (Echoes, Apple TV, Philips TV, Sony TV) — still on legacy `192.168.178/9`, not segmented onto **Consumer 1020** or **IoT 1040**. Tidy-up candidate.
- **Xiaomi Hub 2** — power on + find on UniFi (Wi-Fi), DHCP-reserve, record model/MAC ([notes](devices/xiaomi-smart-home-hub-2/)).
- **Xiaomi thermometers** — check HW revision (B1.6 = can't flash pvvx) before deciding the integration path ([options](devices/xiaomi-lywsd03mmc/)).
- **BLE into HA is unsolved** — HAOS has no Bluetooth radio; a **BLE proxy** (dedicated ESP32/ESPHome — see [`esp-fleet.md`](esp-fleet.md)) is the prerequisite for the thermometers and any future BLE device. ⚠️ The Tube gw's `Esp_Bluetooth` BLE-gateway mode is **not** a safe substitute — it flooded HA offline twice ([details](devices/tube-zb-gw-efr32/#-esp_bluetooth-switch--ble-gateway-mode--it-can-take-ha-down)); left OFF.
- _Resolved 2026-07-18:_ ~~"Tuya device" `10.40.169.147`~~ → Alen air purifier; ~~"Unknown" `10.40.2.131`~~ → Nest Protect.

## Related docs
- [`esp-fleet.md`](esp-fleet.md) — bare ESP boards on hand for DIY builds (BLE proxy, ESL-2, sensors) + which chip does what.

## Notes
- **Integrations at a glance:** Zigbee → **ZHA** (via Tube gw); Matter → matter-server (CT 6001);
  MQTT → broker (CT 6000); Chromecast → AirCast (CT 6002); the C10 → Mate (CT 4100); Tuya/Meross/
  SmartThings/Tapo/Alexa/Nest → their own integrations; IR → Broadlink RM4 mini + SmartIR (**planned** — blaster not installed yet). **ACs split by brand:** the **downstairs Daikin** goes **wired S21 + ESPHome** (not IR — [docs](devices/daikin-ftxf-s21/)); the **upstairs Mitsubishi Heavy Industries** unit → **wired CNS** (MHI-AC-Ctrl-ESPHome, SPI) or IR fallback (TBD).
- The SmartHome **services** themselves (broker/matter/aircast/HA) live in
  [`../README.md`](../README.md), not here.
</content>
