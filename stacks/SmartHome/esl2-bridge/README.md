# SmartHome.ESL-2-Bridge

Local Home Assistant bridge for the **Arrowhead ESL-2 (ELITE-S)** alarm panel via a direct
**keypad-bus tap** (`POS/NEG/CLK/DAT`) — no paid RS232-BD board, no cloud.

> **Status: scaffold.** Code follows the Phase-0 bus capture — see issue **#255**.
> Lives here as a self-contained subfolder for now; **extract to its own repo
> `SmartHome.ESL-2-Bridge` once it grows** (same playbook as youtarr/leapmotor).

- **Protocol + RE research:** [`../docs/devices/arrowhead-esl-2/`](../docs/devices/arrowhead-esl-2/)
- **Tracking:** #255 (build) · #250 (HA→LXC) · #251 (ESPHome) · #252 (Docusaurus)

## Architecture (decided 2026-07-17)

**Hybrid — C++ on the metal, C# for the brains.**

```
ESL-2 keypad bus (5V CLK/DAT)
   │  resistor divider (read) / logic-level converter (control)
   ▼
ESP32  ── firmware/ (C++/ESP-IDF or Arduino) ──►  clean frames  ──► MQTT (broker CT 6000)
   timing-critical CLK/DAT capture (sample on CLK falling edge, HDLC-like decode)
                                                                        │
                                                                        ▼
                                          bridge/ (C# .NET)  ── frame → ESL-2 semantics
                                          (RS232 spec mapping, HA entities, MQTT discovery)
                                          runs on Pi 1 rev B or a SmartHome stack container
```

**Why not C# on the ESP32?** [.NET nanoFramework](https://www.nanoframework.net/) *can* run
C# on an ESP32, but it's a **managed runtime** (GC + interrupt latency) — risky for sampling
a clocked bus edge-by-edge, where a pause drops bits. So the timing-critical front-end is
**C++** (porting [MadDoct/ESP-CrowAlarmInterface](https://github.com/MadDoct/ESP-CrowAlarmInterface)),
and **C#** owns the semantic bridge — where it fits the C#-everywhere homelab
(ProxmoxSharp/SynoSharp/UnifiSharp/engine). Pure-C++ (MadDoct straight port, MQTT direct to HA)
is the fast path if we skip the C# layer.

> Fallback if the C# metal path is ever wanted end-to-end: nanoFramework + offload CLK/DAT to
> a hardware peripheral (SPI-slave/RMT). Capture the bus first before betting on it.

## Planned layout

| Path | Purpose |
|------|---------|
| `firmware/` | ESP32 C++ bus front-end (CLK/DAT capture → frames) |
| `bridge/` | C# .NET service — frame → ESL-2 semantics → HA/MQTT (added once fields are mapped) |
| `hardware/` | Wiring notes / divider + level-converter schematic |

## Next
Phase 0 (issue #255): resistor divider on CLK/DAT → ESP32 (or Pi 1B running `sivann/crowalarm`),
capture `10000001`-flagged frames, map the ESL-2 field layout (16 zones + areas A/B + system
flags) against [`../docs/devices/arrowhead-esl-2/rs232-protocol.md`](../docs/devices/arrowhead-esl-2/rs232-protocol.md).
