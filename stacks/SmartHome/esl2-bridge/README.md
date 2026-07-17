# SmartHome.ESL-2-Bridge

Local Home Assistant bridge for the **Arrowhead ESL-2 (ELITE-S)** alarm panel via a direct
**keypad-bus tap** (`POS/NEG/CLK/DAT`) — no paid RS232-BD board, no cloud.

> **Status: scaffold.** Code follows the Phase-0 bus capture — see issue **#255**.
> Lives here as a self-contained subfolder for now; **extract to its own repo
> `SmartHome.ESL-2-Bridge` once it grows** (same playbook as youtarr/leapmotor).

- **Protocol + RE research:** [`../docs/devices/arrowhead-esl-2/`](../docs/devices/arrowhead-esl-2/)
- **Tracking:** #255 (build) · #250 (HA→LXC) · #251 (ESPHome) · #252 (Docusaurus)

## Architecture (decided 2026-07-17, revised)

**C# end-to-end via [.NET nanoFramework](https://www.nanoframework.net/)** — one ecosystem,
matching the C#-everywhere homelab (ProxmoxSharp/SynoSharp/UnifiSharp/engine).

```
ESL-2 keypad bus (5V CLK/DAT)
   │  resistor divider (read) / logic-level converter (control)
   ▼
ESP32 (nanoFramework, C#)  ── capture CLK/DAT → decode HDLC-like frames → map to ESL-2
   semantics → publish to MQTT (broker CT 6000) → HA
```

**Why C# on the metal is fine here (corrected):** the "managed runtime can't bit-bang" rule
only applies to **MHz-class, sub-µs, cycle-accurate** signalling (e.g. WS2812 LEDs). This bus
is **kHz-class** — evidence: `sivann/crowalarm` keeps up with a *per-edge Python callback on a
700 MHz Pi Model B*, so bit periods are ~**hundreds of µs–1 ms**. A 240 MHz ESP32 under
nanoFramework has ample margin. Confirm the real clock rate at Phase-0 capture.

**GC is the only gremlin — mitigations (any one suffices):**
1. **Allocation-free capture loop** (pre-allocated buffers, no per-bit `new`) → GC ~never runs in the hot path.
2. **Offload to a hardware peripheral** — CLK/DAT into ESP32 **SPI-slave / RMT**; hardware clocks the bits, C# reads finished buffers → immune to GC/jitter.
3. **Self-framing protocol** — the `10000001` flag means a rare dropped frame just resyncs next frame; fine for a status bridge.

> C++ (porting [MadDoct/ESP-CrowAlarmInterface](https://github.com/MadDoct/ESP-CrowAlarmInterface))
> is only the fallback if Phase-0 shows a surprisingly fast bus that even the HW-peripheral path
> can't keep up with in C#. Not expected.

## Planned layout

| Path | Purpose |
|------|---------|
| `firmware/` | ESP32 **C# / nanoFramework** — CLK/DAT capture → HDLC decode → ESL-2 semantics → MQTT |
| `hardware/` | Wiring notes / divider + level-converter schematic |

> A separate C# host `bridge/` is only needed if we later move the semantic layer off the
> ESP32 (e.g. onto the Pi 1B or a stack container). Default is all-on-the-ESP32 in C#.

## Next
Phase 0 (issue #255): resistor divider on CLK/DAT → ESP32 (or Pi 1B running `sivann/crowalarm`),
capture `10000001`-flagged frames, map the ESL-2 field layout (16 zones + areas A/B + system
flags) against [`../docs/devices/arrowhead-esl-2/rs232-protocol.md`](../docs/devices/arrowhead-esl-2/rs232-protocol.md).
