# ELITE-S / ESL-2 — RS232-BD V2 protocol reference

Transcribed from the Arrowhead **RS232-BD V2** manual ([`RS232-BD-V2-protocol.pdf`](RS232-BD-V2-protocol.pdf)).
This is the **RS232 side** the board translates the keypad bus to/from — our ESP32
bus-tap aims to reproduce these semantics from the raw CLK/DAT bus.

## Link layer
- **9600 baud, ASCII, 8N1.** Messages are **UPPERCASE**. One event per line (new line each).
- Board taps keypad bus **POS / NEG / CLK / DAT**; set to an **unused keypad address** via DIP (KP#1–8).
- Serial lead: **Board Tx → DB9 pin 2, Rx → pin 3, GND → pin 5** (female DB9).
- On-board LED flashes ~½ s when connected/working.

### Keypad address DIP (SW1/2/3; SW4–8 unused)
| KP# | 1 | 2 | 3 |  | KP# | 1 | 2 | 3 |
|----|---|---|---|--|----|---|---|---|
| 1 | OFF | OFF | OFF | | 5 | OFF | OFF | ON |
| 2 | ON | OFF | OFF | | 6 | ON | OFF | ON |
| 3 | OFF | ON | OFF | | 7 | OFF | ON | ON |
| 4 | ON | ON | OFF | | 8 | ON | ON | ON |

---

## Messages FROM the panel (events)

### Zones (`xxx` = zone 001–016)
| Msg | Meaning |
|-----|---------|
| `ZOxxx` | Zone **open** |
| `ZCxxx` | Zone **closed** |
| `ZAxxx` | Zone **alarm** |
| `ZRxxx` | Zone alarm **restore** |
| `ZTxxx` | Zone **trouble** |
| `ZTRxxx` | Zone trouble **restore** |
| `ZBYxxx` | Zone **bypassed** |
| `ZBYRxxx` | Zone bypass **restore** |
| `ZBL` | RF zone **battery low** (no zone #) |
| `ZBR` | RF zone battery **OK** (no zone #) |
| `ZSAxxx` | RF zone **supervise fail** |
| `ZSRxxx` | RF zone supervise **OK** |
| `ZIA` | Zone **sensor-watch alarm** (no zone #; also emits a `ZT` w/ the zone) |
| `ZIR` | Zone sensor-watch **OK** (no zone #) |

> Multiple simultaneous zones → one line each. `ZO` in the tables = the letter O (**Z-O**pen).

### Arm / disarm
| Msg | Meaning | | Msg | Meaning |
|-----|---------|-|-----|---------|
| `AA` | Armed Area A | | `EAA` | Area A arming (exit delay) |
| `AB` | Armed Area B | | `EAB` | Area B arming (exit delay) |
| `SA` | Stay-armed Area A | | `ESA` | Area A stay arming (exit delay) |
| `SB` | Stay-armed Area B | | `ESB` | Area B stay arming (exit delay) |
| `DA` | Disarmed Area A | | | |
| `DB` | Disarmed Area B | | | |

> Arming one area also reports the other's state, e.g. `EAA` then `DB`.

### System
| Msg | Meaning | | Msg | Meaning |
|-----|---------|-|-----|---------|
| `MF`/`MR` | Mains fail / restore | | `FF`/`FR` | Fuse or output fail / restore |
| `BF`/`BR` | Battery fail / restore | | `PBF`/`PBR` | Pendant battery fail / restore |
| `TA`/`TR` | Panel tamper alarm / restore | | `CTF`/`CTR` | Code tamper fail / restore |
| `LF`/`LR` | Telephone line fail / restore | | `RIF`/`RIR` | Receiver fail / restore |
| `DF`/`DR` | Dialler fail / restore | | `CAL`/`CLF` | Dialler active (calling) / inactive |
| `RO` | Ready On (all zones sealed) | | `NR` | Not Ready (zones unsealed) |

### Outputs (`xxx` = output 001–008)
| Msg | Meaning |
|-----|---------|
| `OOxxx` | Output **On** |
| `ORxxx` | Output **Off** (restore) |

### Receiver signals
| Msg | Meaning |
|-----|---------|
| `RSO.xx.xx.xx.xx.xx.` | Received code **On** — 5 × 2-digit hex (`0–9,A–F`), dot-separated |
| `RSR.xx.xx.xx.xx.xx.` | Received code **Off** |

---

## Commands TO the panel
Case-sensitive, **UPPERCASE**, sent as one complete string, then a real keyboard
**`<ENTER>`** to transmit (the literal `E` in a string = the panel's ENTER *key*, not the
line terminator). Accepted commands echo **`OK!!`** — resend if not seen.

### `KEYS_` — press keypad keys
`KEYS_<keys>E` + `<ENTER>` — mimics keypad presses. No spaces.
- Example: `KEYS_123E` → panel receives `123` + ENTER.
- Example: `KEYS_R` → ARM button.
- Zone bypass: **two-digit** zone, e.g. bypass zone 6 → `KEYS_X06E`.
- Output control: **single-digit** output, e.g. toggle output 7 → `KEYS_C7E`.
- Panel buffers commands 40 s; if a string was sent without its `E`, send `KEYS_E` to
  flush the panel keypad buffer before retrying (else keys concatenate, e.g. `123123E`).
- Program mode via RS232 is possible but **discouraged** (no feedback; forgetting
  `KEYS_PE` to exit locks program mode + inhibits alarms/arming).

#### Key value list
| Key | | Key | | Key | | Key |
|--|--|--|--|--|--|--|
| `0`–`9` = digits | | `C` = CONTROL | | `N` = PANIC | | `R` = ARM |
| `S` = STAY | | `X` = EXCL | | `P` = PROG | | `E` = ENTER |
| `A` = A | | `B` = B | | `H` = CHIME | | |

### `NEWCODE#_<code>E` — store a local code
Save up to **10** codes (slots `0–9`, each 1–6 digits) on the board itself; the same code
must exist in the panel. `#` = slot, `_` separator, then digits, then `E` (panel ENTER),
then `<ENTER>`. Echoes `OK!!`.

### `CODE#` — send a stored code
`CODE#` + `<ENTER>` — sends stored slot `#` (0–9) down the bus to arm/disarm/operate outputs.

### `MEM` — event memory
`MEM` + `<ENTER>` enters memory mode; each subsequent `MEM<ENTER>` shows the next event
(time+date+description), up to 255. Terminate with `E` + `<ENTER>`.
> If the panel is **armed**, memory retrieval requires the board at **KP#8**.

### `?` — status poll
`?` + `<ENTER>` → panel dumps current state (e.g. `DA`, `DB`, `MF`, plus any unsealed
zones / active outputs). This is the "get everything now" command — ideal on (re)connect.
