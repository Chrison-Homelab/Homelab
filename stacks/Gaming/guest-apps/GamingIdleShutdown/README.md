# GamingIdleShutdown

A wee C#/**Avalonia** tray app that runs **inside the Windows gaming guest** (VM 1002)
and cleanly shuts it down when nobody's using it — freeing the GPU + 12 GiB of RAM
on the shared, RAM-tight desktop-01. When it detects idle it shows a small
always-on-top **countdown you can Cancel / Snooze / Shut down now**, so it never
pulls the rug while you're playing (or actively idling).

Story: [#144](https://github.com/Chrison-dev/Homelab/issues/144). Part of the
Gaming stack's guest-side seam — like [`../../guest-setup/`](../../guest-setup/),
it's re-installed after a throwaway-OS rebuild; the converge engine doesn't touch
the guest.

## Why Avalonia (not WPF)
WPF is Windows-only — it can't build on the macOS dev box or the Linux CI. Avalonia
is cross-platform C# XAML, so it builds/tests anywhere and still deploys to Windows.
The only Windows-specific bits (input-idle `GetLastInputInfo`, `shutdown /s`) are
guarded with an OS check.

## How it decides "idle"
Every `PollSeconds` it samples three signals; the VM is **active** if *any* of:
- keyboard/mouse input within the last ~2 polls (`GetLastInputInfo`), **or**
- CPU ≥ `CpuThresholdPercent` (a running game pegs it), **or**
- network ≥ `NetThresholdKBps` (a Steam Remote Play stream pegs it).

Idle for `IdleMinutes` straight ⇒ the countdown appears. Expiry ⇒ clean
`shutdown /s /t 0` (only fires when CPU is already low, so nothing's mid-save).

## Config (`config.json` beside the exe — all optional)
| key | default | meaning |
|-----|---------|---------|
| `pollSeconds` | 60 | sample interval |
| `idleMinutes` | 30 | continuous idle before the countdown |
| `cpuThresholdPercent` | 10 | below this = idle |
| `netThresholdKBps` | 256 | below this = idle |
| `countdownSeconds` | 300 | countdown length |
| `snoozeMinutes` | 60 | Snooze duration |
| `dryRun` | **true** | log "would shut down" instead of shutting down |

> **`dryRun` is true by default** — a fresh deploy only *observes*. Watch the log,
> tune thresholds, then set `dryRun: false` to arm it (issue #144, Phases 2→3).

Example to arm it with a 20-minute idle window:
```json
{ "dryRun": false, "idleMinutes": 20 }
```

## Build & deploy
Build anywhere with the .NET 8+ SDK; publish a self-contained Windows binary (no
runtime needed in the guest):
```bash
dotnet publish -c Release -r win-x64 --self-contained -o ./publish
```
Copy `./publish/` into the guest, then in the **gamer's session** (no elevation):
```powershell
cd <publish folder>
./Install.ps1        # registers HKCU autostart + launches it
```

## Operating it
- **Tray icon** → *Snooze 1h* or *Quit*.
- **Countdown window** → *Cancel* (reset), *Snooze 1h*, *Shut down now*.
- **Log:** `%LOCALAPPDATA%\GamingIdleShutdown\log.txt` (every decision + the deciding metrics).

## Scope / caveats
- VM 1002 (Windows) only for now; 1003/bazzite is a later, separate effort.
- Auto-**start**/wake-on-demand is out of scope — this only shuts *down*.
- Not wired into CI: `net*-windows`… actually this targets plain `net8.0` and builds
  cross-platform, but it's a guest app deployed by hand, so it's intentionally
  outside the converge/deploy pipelines.
