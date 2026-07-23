# PowerOrchestrator

Demand-driven node power management for the Proxmox fleet ([#191](https://github.com/Chrison-Homelab/Homelab/issues/191)):
sleep heavy nodes when nobody's using them, wake them on demand. Full-stack C# (`net10.0`),
dogfooding **ProxmoxSharp** + **UniFiSharp**. The repo's first long-running Generic-Host service.

**PR1** shipped the Core domain + worker service (with OpenTelemetry). **PR2** adds a **Blazor web
dashboard** on the same host (control + monitor at `/`). PR3 arms automatic sleep once the blockers clear.

## How it works

Every poll (~60 s) the service samples two signals and runs a per-node policy:

- **Presence** (`UnifiPresenceProvider`) — is a tracked MAC (your phone) on the network? Via
  UniFiSharp's connected-clients list. No tracked MACs ⇒ always "away".
- **Idle** (`ProxmoxIdleProvider`) — is the node online and how many guests run? Via a ProxmoxSharp
  cluster discovery. Idle = 0 running guests.

```
present + node offline           → Wake   (WoL magic packet)
everyone away + idle ≥ debounce  → Sleep  (stop guests via Proxmox API, then host poweroff via SSH)
otherwise                        → NoOp
```

### Dry-run by default (and why)

`ORCH_ARMED=false` (default) means the **automatic** loop only *logs* the decision it would take
and emits metrics — it never powers anything off. This is load-bearing: `desktop-01` still hosts
always-on services (cloudflared, forgejo + runners, ERP, topaz), so auto-sleep stays disarmed until
those are evacuated and cluster quorum is hardened with a QDevice (the #191 blockers, encoded in
`ArmGuard`). `POST /policy/arm` returns `409` listing the unmet preconditions until then.

**Manual operator commands act for real** — wake works today, and sleep replays the exact sequence
proven by hand on desktop-01 (guests stopped gracefully, then `poweroff`).

## Web dashboard

`GET /` — a Blazor (Interactive Server) control + monitor page: armed/dry-run badge, presence +
away-timer, a card per managed node (online/asleep · running guests · last decision + reason), and
**Wake / Sleep / Arm** buttons. Sleep is behind a confirm; Arm is disabled with the #191 blocker
reasons shown. Live status refreshes every ~3s. Rich charts still come from Grafana (via OTel).

## HTTP API

| Method | Path | Effect |
|--------|------|--------|
| GET  | `/` | Blazor dashboard (control + monitor) |
| GET  | `/healthz` | liveness |
| GET  | `/status` | current world-view (armed, presence, per-node state + last decision, arm preconditions) |
| POST | `/nodes/{node}/wake` | **real** — WoL magic packet |
| POST | `/nodes/{node}/sleep` | **real** — stop guests + poweroff (managed, non-sentinel nodes only) |
| POST | `/policy/arm` | 409 + unmet preconditions while #191 blockers stand |

## Run locally (dry-run, read-only)

```bash
set -a && . ./secrets.env && set +a          # PROXMOX_* + UNIFI_* + ORCH_*
dotnet run --project tools/PowerOrchestrator/src/PowerOrchestrator.Service
# Dashboard: http://localhost:8080/   ·   API: http://localhost:8080/status
curl -s localhost:8080/status | jq
```

With `ORCH_ARMED` unset it never powers off. Set `ORCH_PRESENCE_MACS` to your phone's MAC to see
real presence flips in `/status`.

## Deploy to nuc-01

Driven by the repo's [Fallout build](../../build) — Fallout owns build → test → publish (native
`dotnet`); the node-side copy + systemd wiring is the `deploy/deploy.sh` sugar:

```bash
./build.sh DeployPowerOrchestrator            # publish linux-x64 → copy to node → systemd enable --now
ssh root@nuc-01 'journalctl -u power-orchestrator -f'
```

(`./build.sh PublishPowerOrchestrator` stops after producing `publish/`; `deploy/deploy.sh` then
copies that onto the node — it does not build.) Installs as a systemd service
(`/opt/power-orchestrator`), dry-run until you set `ORCH_ARMED=true` in
`/opt/power-orchestrator/power-orchestrator.env` and restart.

## Build & test

```bash
./build.sh CompilePowerOrchestrator           # dotnet build the solution
./build.sh TestPowerOrchestrator              # + xUnit suite (what CI runs)
```

## Telemetry

The service exports OTLP (metrics + traces) when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The
collector lives in [`stacks/monitoring`](../../stacks/monitoring) (`otel-collector` → Prometheus / Tempo /
Loki) with a **Node Power** Grafana dashboard. Metrics: `orchestrator_armed`,
`orchestrator_presence_present_count`, `orchestrator_node_online{node}`,
`orchestrator_node_running_guests{node}`, `orchestrator_actions_total{node,action,trigger,result}`.

## Layout

```
src/PowerOrchestrator.Core      domain: config, presence, idle, WoL, sleep, policy, arm-guard
src/PowerOrchestrator.Service   ASP.NET host: BackgroundService loop + control API + OTel + Blazor (Components/)
src/PowerOrchestrator.Tests     xUnit: policy/debounce, WoL bytes, options, arm-guard
deploy/                         systemd unit + deploy.sh (copy + systemd; build is Fallout's job)
```

Build/test/publish/deploy targets live in the repo's Fallout build ([`build/Build.cs`](../../build/Build.cs)):
`CompilePowerOrchestrator` → `TestPowerOrchestrator` → `PublishPowerOrchestrator` → `DeployPowerOrchestrator`.

## Roadmap

- ✅ **PR1** — Core + worker (dry-run) + OTel.
- ✅ **PR2** — Blazor web dashboard on the same host (control buttons + live status).
- **PR3** — arm automatic sleep after the #191 blockers (service evacuation off desktop-01 + QDevice).
- Optional: graduate the host poweroff to a ProxmoxSharp `NodeWriter.ShutdownAsync`.
