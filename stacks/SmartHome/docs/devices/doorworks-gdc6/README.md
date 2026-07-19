# Doorworks GDC6 garage-door opener

Sectional garage-door opener by **Doorworks** (NZ). **Dumb by design** — no cloud,
no API, no RF-rolling-code to decode. Goal: put the door in **Home Assistant** as a
proper `cover` (open / close / stop + real state) by driving its **low-voltage
wall-button contact** with an **ESP8266** running **ESPHome**, plus our own
**position sensor** — since the opener gives **zero** state feedback.

Because it's a plain dry-contact opener, this is the **universal** approach (pulse a
contact + sense the door), **not** a ratgdo/Security+ reverse-engineering job.

## What we have
- **Unit:** Doorworks **GDC6** controller, wall-mounted in the garage next to a single
  switched GPO (the opener's mains plug occupies it).
- **Board (from the fleet):** an **ESP8266MOD** — [`esp-fleet.md`](../../esp-fleet.md)
  earmarks the ESP8266s for exactly this ("relays / buttons / simple sensors"), keeping
  the ESP32 / C6 boards free for the BT-proxy and ESL-2 jobs that need Wi-Fi+BLE.
- **Network reach:** IoT VLAN **1040** already serves the garage (Meross plug
  `10.40.170.241`, Tube ZB gw) — the node joins 1040 Wi-Fi, native ESPHome API to HA,
  managed from the **ESPHome LXC (CT 6003)**.

## How a dumb opener integrates (the two taps)
1. **Trigger** — the opener has a **momentary dry-contact** wall-button terminal.
   Shorting it = "press the button." We wire a **relay or optocoupler in parallel**
   with the existing button and pulse it (~500 ms) from an ESP GPIO.
2. **State** — the opener reports nothing, so we add a **magnetic reed switch** on the
   door: one at the closed position = closed/not-closed; add a second at the open
   position for full **open / closed / moving**. ESPHome models this as a `cover` with
   endstop binary sensors (the canonical ESPHome garage cookbook).

## Power (decided: 5V USB plugpack; buck-tap only if a clean rail turns up)
Mains-powered, always-on node — **no batteries.**

- **Default — 5V USB plugpack → GPO.** The generic ESP8266MOD (NodeMCU/Wemos-class)
  has an onboard 3.3V regulator and takes **5V over USB**. The opener's GPO looks
  **single + occupied**, so add a **double-adapter/piggyback plug** (reversible, no
  tools) or have a sparky fit a **double GPO** (tidier). Use a **good** PSU — cheap
  ones brown-out the ESP into random reboots, worse next to a motor.
- **Alt — buck off the opener's LV rail.** *If* Stage 0 finds a **clean DC accessory /
  battery-backup terminal** (sectional openers often carry ~24V), an **MP1584 / mini-360**
  buck → 5V/3.3V gives a one-box, no-extra-plug install (and rides a power cut if a backup
  battery exists). Not the plan until confirmed — and don't load the backup circuit.
- **Relay-coil synergy:** powering at **5V USB** also feeds a standard **5V relay
  module** coil (VCC off the ESP Vin/5V pin). An **optocoupler (PC817)** trigger needs
  **no** coil power (runs off 3.3V) and is more power-flexible — pick in Stage 0.

## The plan (staged)

**Stage 0 — Recon / teardown (no build).** Open the GDC6 and document:
- The **wall-button terminals** — confirm momentary dry contact; **measure the voltage**
  across them (expect low DC) and whether it's **OSC single-button** (one button cycles
  open→stop→close→stop) or **separate open/close**.
- Any **DC accessory / battery-backup terminal** + its voltage (→ power option B).
- **Trigger tech choice:** relay (true dry mechanical contact, safest for an unknown
  circuit) vs optocoupler (silent, isolated, 3.3V-only).
- Where a **reed switch** can mount along the door's travel.

**Stage 1 — Bench prototype (no opener).** ESP8266MOD + chosen trigger part on the
bench; flash via ESPHome LXC (6003); join IoT 1040 Wi-Fi; appears in HA as a template
`cover`. Verify the **pulse fires** (LED/multimeter, *not* wired to the opener yet) and
that **OTA + HA entity** work end-to-end.

**Stage 2 — Trigger wiring (actuation).** Wire the relay/opto **in parallel with the
wall button**; finalise power (option A, or B if Stage 0 found a clean rail). Confirm
**HA button → door moves.** Debounced ~500 ms pulse.

**Stage 3 — Position sensing.** Add the **closed** reed switch → real state; upgrade the
`cover` to report open/closed via endstop. (Second reed → full open/closed/moving is a
tracked follow-up — see Next steps.)

**Stage 4 — HA integration & automations.** "**Garage left open**" notification,
optional **auto-close after N minutes**, remote control via the existing HA remote-access
path. **Security:** it's a physical-security actuator — stays behind HA auth on the
firewalled **IoT 1040**; the relay is never exposed directly.

**Stage 5 — Productionise.** Enclosure + tidy mount, keep the PSU/ESP clear of the
motor (EMI), and land the final `cover` config in the fleet. This is an ESPHome-managed
node (CT 6003), so it's doc + firmware — not a converge stack member.

## ⚠️ Safety
- **Only touch the low-voltage button/accessory terminals — never mains.** The opener's
  240V side is out of scope; a sparky does any GPO work.
- **Keep the opener fully functional standalone** — the physical wall button and remotes
  must keep working with our contact wired in parallel.
- A garage door is a **safety actuator**: keep the pulse momentary, verify the door's own
  safety-reverse/beam still works, and never auto-operate without state confirmation.

## Next steps
1. **Stage 0 teardown** — measure the wall-button terminals; pick relay vs opto; check for
   a DC accessory rail; confirm OSC vs separate open/close; scope reed-switch mount points.
2. **Stage 1 bench build** — first-pass ESPHome `cover` config; prove pulse + HA entity + OTA.
3. Wire trigger (Stage 2) → add closed reed (Stage 3) → automations (Stage 4).
