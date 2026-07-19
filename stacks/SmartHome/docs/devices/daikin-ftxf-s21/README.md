# Daikin FTXF50TVMA (Cora) — local control via the S21 port

The **downstairs** air conditioner: a **Daikin FTXF50TVMA** (Cora series, 5.0 kW
reverse-cycle inverter split; indoor unit serial `E009281`, made in Thailand).
Goal: a **fully local, cloud-free** Home Assistant climate entity with **real
state feedback** — by tapping the unit's **S21 serial port** with a **XIAO
ESP32-C6** running **ESPHome**, instead of an open-loop IR blaster.

> **Why not IR?** The other ACs are slated for Broadlink + SmartIR (IR). IR is
> fire-and-forget: HA never knows the unit's *actual* mode/setpoint, and it drifts
> the moment anyone touches the handset — which is exactly the unreliability we hit
> before. Daikin is the one brand where a **wired** tap is easy, so this unit gets
> the good path. (Upstairs is **Mitsubishi Heavy Industries** → a *different* wired
> route, see [below](#the-upstairs-unit-is-different-mhi--cns).)

## Why the S21 route works here

Daikin indoor units expose an **S21** connector on the indoor PCB — the white
5-pin JST port that the official `BRP072A42/BRP072C42` Wi-Fi module plugs into.
The open-source community taps that same port for **bidirectional** control. Our
FTXF50 is in the Cora/FTXF family that's on Faikin's
[confirmed-working list](https://codeberg.org/RevK/ESP32-Faikout/wiki/List-of-confirmed-working-air-con-units).

| | IR blaster (Broadlink + SmartIR) | **S21 wired (this doc)** |
|---|---|---|
| Direction | Open-loop, fire-and-forget | **Bidirectional** — reads real state |
| State in HA | *Assumed* (drifts on handset use) | **Actual** mode/setpoint + onboard sensors |
| Reliability | Line-of-sight, drops commands | Wired UART — doesn't miss |
| Cloud | none | none |
| Sensors | none | inside / outside / coil temp, fan RPM |

## Firmware: ESPHome `daikin_s21` (decided)

Two viable stacks tap S21; we're going **ESPHome** because it lands as a native HA
climate entity via our planned **ESPHome LXC (CT 6003, [#251](https://github.com/Chrison-dev/Homelab/issues/251))**
and keeps the fleet uniform (same builder/dashboard as every other DIY node).

- **Chosen:** [`joshbenner/esphome-daikin-s21`](https://github.com/joshbenner/esphome-daikin-s21)
  — an ESPHome external component. Config lives at
  [`esphome-daikin-downstairs.yaml.example`](esphome-daikin-downstairs.yaml.example).
- **Alternative (not chosen):** [RevK's Faikin/Faikout](https://codeberg.org/RevK/ESP32-Faikout)
  — purpose-built firmware **+ open-hardware PCB**. Excellent and bulletproof, but
  it's its own MQTT-based firmware/ecosystem; ESPHome fits our stack better. We
  still **borrow its open hardware** if we ever want a real board (see
  [`hardware/`](hardware/)).

## The wiring in one line

S21 is a **2400 baud 8E2 UART at 5 V logic**; the XIAO ESP32-C6 is **3.3 V** → a
**bidirectional level shifter** sits between them. Full pinout, BOM and diagram:
**[`hardware/README.md`](hardware/README.md)**.

## How this fits the SmartHome stack

```
Daikin FTXF50 ──S21 (5V UART)──▶ level shifter ──3.3V──▶ XIAO ESP32-C6 (ESPHome)
                                                              │ native ESPHome API (Wi-Fi 6, IoT 1040)
                                                              ▼
                                              ESPHome LXC 6003 ──▶ Home Assistant (VM 2000)
                                                                     climate.downstairs_ac
```

- **Network:** the C6 joins **IoT VLAN 1040** (where the ESPHome LXC + HA's IoT
  reach live); DHCP-reserve it like the other members.
- **Transport:** ESPHome **native API** (not MQTT) — HA auto-discovers the node.
  No broker dependency, though it *could* publish to CT 6000 if ever wanted.

## The upstairs unit is different (MHI → CNS)

Per [`../../devices.md`](../../devices.md) the **upstairs** AC is **Mitsubishi Heavy
Industries (MHI)** — a *different company* from Mitsubishi Electric, with a different
protocol. It uses **neither** S21 **nor** the Mitsubishi Electric CN105. MHI
SRK-series units expose a **CNS** connector (5-pin JST XH, 2.5 mm pitch) speaking an
**SPI** protocol (the AC is SPI master, the ESP is slave), tapped by
[`ginkage/MHI-AC-Ctrl-ESPHome`](https://github.com/ginkage/MHI-AC-Ctrl-ESPHome)
(the ESPHome port of [`absalom-muc/MHI-AC-Ctrl`](https://github.com/absalom-muc/MHI-AC-Ctrl);
there's even an open [MHI-AC-Ctrl PCB](https://olliver.gitlab.io/MHI-AC-Ctrl_PCB/), the
MHI equivalent of Faikin). So the "**one XIAO C6 per unit**" plan still holds — same
outcome (local ESPHome climate entity, no cloud), just a different component +
connector than the Daikin. ⚠️ **MHI gotcha:** a failed handshake can lock the AC's
internal bus until you cut mains at the breaker for ~30 s. That build gets its own
device doc when we tackle it; this doc is Daikin-only.

## Plan / checklist

- [ ] Order the [BOM](hardware/README.md#bill-of-materials) (level shifter, JST
      EHR-5 pigtail, optional buck). C6 already on hand (ESP fleet).
- [ ] Stand up the **ESPHome LXC** ([#251](https://github.com/Chrison-dev/Homelab/issues/251))
      — prerequisite for OTA-flashing + the native API.
- [ ] Bench-wire C6 ↔ level shifter ↔ a 5 V UART loopback; flash
      [`esphome-daikin-downstairs.yaml.example`](esphome-daikin-downstairs.yaml.example); confirm the
      node boots + joins Wi-Fi.
- [ ] **Power off at the isolator**, pop the FTXF front grille, locate the white
      **S21** connector on the indoor PCB, plug in the pigtail.
- [ ] Power up; verify HA shows `climate.downstairs_ac` with live mode/temp; test a
      setpoint + mode change end-to-end.
- [ ] DHCP-reserve the C6 on IoT 1040; record MAC/IP in [`../../devices.md`](../../devices.md).
- [ ] (Optional) tidy into a small enclosure; decide custom-PCB vs perfboard
      ([`hardware/`](hardware/)).

## ⚠️ Safety & notes

- **Mains equipment.** Only open the unit with the **circuit isolated/powered off**.
  The S21 tap itself is low-voltage and **fully reversible** (unplug → stock) — it
  fits the repo's add-only, removable philosophy.
- **Landlord vs owned:** if this unit isn't ours, note the (reversible) tap before
  committing — it changes nothing technically.
- **`>5 V` on S21 pin 4.** Measure it before using it to power anything; regulate to
  5 V if feeding the C6. Details + safe options in [`hardware/`](hardware/).
- **Logic inversion:** most ESPHome builds use a straight bidirectional level shifter
  and configure the UART normally; RevK's board instead inverts RX via a FET. If the
  component can't sync, that inversion is the first thing to check — see
  [`hardware/README.md`](hardware/README.md#gotchas).

## Sources
- [`joshbenner/esphome-daikin-s21`](https://github.com/joshbenner/esphome-daikin-s21) — the chosen ESPHome component (pinout, YAML, supported modes).
- [Daikin + ESP32 + ESPHome = Local Control!](https://community.home-assistant.io/t/daikin-esp32-esphome-local-control/699209) — HA community write-up.
- [RevK ESP32-Faikout — Wiring](https://codeberg.org/RevK/ESP32-Faikout/wiki/Wiring) and [confirmed units](https://codeberg.org/RevK/ESP32-Faikout/wiki/List-of-confirmed-working-air-con-units).
- Daikin `BRP072A42` install guide (S21 connector location behind the front grille).
</content>
</invoke>
