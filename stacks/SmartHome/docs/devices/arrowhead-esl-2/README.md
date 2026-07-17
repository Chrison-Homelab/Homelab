# Arrowhead ESL-2 (ELITE-S) alarm panel

Home security panel by **Arrowhead Alarm Products** (NZ). **Discontinued / effectively
abandonware.** Goal: integrate it into Home Assistant **locally** (zone status,
arm/disarm, outputs, system health) **without** buying the paid RS232 board and
**without** the cloud — by tapping the panel's keypad bus with an **ESP32**.

## What we have
- **RS232-BD V2 protocol manual** ([`RS232-BD-V2-protocol.pdf`](RS232-BD-V2-protocol.pdf))
  from Arrowhead support — fully documents the RS232 ASCII protocol the board
  emits/accepts. Transcribed to [`rs232-protocol.md`](rs232-protocol.md).
- **Hardware on hand:** several **ESP32**, one **Raspberry Pi 1 rev B**.
- Panel + keypad bus (physical access at home).

## How the official path works (the board we're avoiding)
The **RS232-BD** is a small board that:
1. Connects to the ELITE-S **keypad bus** — 4 wires: **POS, NEG** (power) + **CLK, DAT** (clocked synchronous serial).
2. **Impersonates a keypad** at an unused address (DIP switches → KP#1–8; must be an *unused* address or the bus conflicts).
3. Translates the keypad bus ↔ **RS232 ASCII, 9600 8N1** (DB9: Tx→pin2, Rx→pin3, GND→pin5).

So the board's *entire paid value is the bus↔RS232 translation.* The manual documents
the **RS232 side** (every event + command) — see [`rs232-protocol.md`](rs232-protocol.md).

## The reverse-engineering plan (skip the board)
Tap the **keypad CLK/DAT bus directly with an ESP32** and reimplement the translation.
We already know the *semantics* (the manual); we only need to reverse the *framing* by
correlating captures with known events (open a zone → watch CLK/DAT).

**Phase 0 — scope the bus (do this first).**
- Measure **POS/NEG/CLK/DAT** voltages. ⚠️ **POS is ~12 V** — the keypad bus logic is
  almost certainly **NOT 3.3 V**. Do **NOT** wire CLK/DAT straight to an ESP32 GPIO
  until measured; use a divider / level shifter / opto based on real levels.
- Capture CLK+DAT with a logic analyser (or a 5 V-tolerant sniffer) while triggering
  known events, to learn the clock rate + frame format.

**Phase 1 — passive sniff (read-only, lowest risk).**
- ESP32 reads CLK/DAT, decodes frames, maps to the documented events (zones, arm state,
  mains/battery/tamper, outputs) → publishes to **MQTT (broker CT 6000)** → HA. No bus
  writes, so zero risk to the live alarm function.

**Phase 2 — active (keypad emulation).**
- Like the board: claim an **unused keypad address** and *write* on DAT to send `KEYS_…`
  equivalents (arm/disarm/outputs, the `?` status poll). Trickier (bus timing + avoiding
  conflicts); only after Phase 1 is solid.

**Firmware:** ESPHome custom component or a small ESP-IDF/Arduino sketch → MQTT → HA.
Fits the SmartHome stack (new member later; see stack README).

> **Alternative if the bus proves nasty:** the board *does* speak the fully-documented
> RS232 ASCII protocol — an ESP32 + MAX3232 + a genuine RS232-BD is the trivial fallback.
> But the whole point here is to avoid the purchase, so bus-tap first.

## Prior art (evaluate before building)
- **[thanoskas/arrowhead_alarm](https://github.com/thanoskas/arrowhead_alarm)** — HA integration for Arrowhead panels (zones/arm/disarm/outputs). **Check its transport** — if it parses this same RS232 ASCII protocol, reuse its parser/state model.
- **[febalci/ha_pycrowipmodule](https://github.com/febalci/ha_pycrowipmodule)** — Crow/AAP IP-module component (needs the IP module + special AAP firmware). AAP == Arrowhead; useful protocol insight.
- **[ankohanse/hass-elite-cloud](https://github.com/ankohanse/hass-elite-cloud)** — ESL/ESL-2/Elite via **Elite Cloud** (cloud dependency — we're avoiding, but confirms ESL-2 is integrable).
- Manuals: [RS232-BD manual (manuals.plus)](https://manuals.plus/arrowhead-alarm/rs232-bd-elite-s-keypad-manual) · [ESL-2 install/programming (ManualsLib)](https://www.manualslib.com/manual/2040574/Arrowhead-Alarm-Products-Esl-2.html) — grab the ESL-2 programming manual for the keypad-address options (`P71–P93E`) an emulated keypad needs.
- Community: [HA forum — EliteControl (NZ)](https://community.home-assistant.io/t/question-about-integrating-elitecontrol-alarm-system-into-home-assistant-nz-company/402663) · [Geekzone — Arrowhead HomeKit](https://www.geekzone.co.nz/forums.asp?forumid=73&topicid=306147).

## Next steps
1. **Scope the keypad bus** (Phase 0) — voltages + a CLK/DAT capture during a zone open + an arm.
2. Pick level-shifting for the ESP32 based on measured CLK/DAT levels.
3. Decode the framing; correlate to [`rs232-protocol.md`](rs232-protocol.md) semantics.
4. Prototype **passive sniff → MQTT → HA** (read-only).
5. Evaluate `thanoskas/arrowhead_alarm` for reuse.
6. Later: Phase 2 keypad emulation for control.

## ⚠️ Safety
It's a **live security panel**. Bus-tap **read-only first**; never disrupt the panel's own
monitoring/dialler. The panel keeps functioning independently of anything we attach.
