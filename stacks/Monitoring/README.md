# Monitoring stack

The homelab's observability host. One member: **`podman-host` (CT 4001)** on `hpe-01`, a
rootless-podman host running the fleet as quadlets.

> ⚠️ **This host watches everything else.** Converging it briefly blinds monitoring — including
> the Prometheus that would otherwise tell you the converge broke something. It is also
> PowerOrchestrator's OTLP sink (#191), so that telemetry gaps for the duration.

## What runs here

| Container | Purpose | Reached at |
|---|---|---|
| `prometheus` | metrics + alert rule evaluation | `:9091` (host), `prometheus:9090` (network) |
| `grafana` | dashboards, and a UI for Alertmanager silences | `:3000` |
| `alertmanager` | the alert bus — routing, grouping, inhibition, silences | `:9093` |
| `karma` | read-only dashboard over Alertmanager | `:8080` |
| `unpoller` | UniFi gear → Prometheus | `unpoller:9130` |
| `snmp_exporter` | Synology NAS via SNMP | `:9116` |
| `exportarr-{radarr,sonarr,prowlarr}` | *arr app metrics | `:9708–9710` |
| `podman-exporter` | container metrics for this host | `:9882` |
| `otel-collector` | OTLP ingest → Tempo/Loki, re-exports to Prometheus | `:4317/:4318`, `:8889` |
| `tempo` / `loki` | traces / logs | via Grafana datasources |
| `pulse` | Proxmox + NAS fleet monitoring (its own UI and alert engine) | `:7655` |

Full per-container detail — uid mappings, why each one does or does not get `UserNS`, and the
cutover notes — is in [`podman-host/README.md`](podman-host/README.md).

## How it is deployed

```bash
./build.sh Preview --stack Monitoring   # dry-run
./build.sh Deploy  --stack Monitoring   # live apply
```

Config is **not** baked into the guest. The tree under `podman-host/assets/` is rendered onto
the host before any unit starts (ADR-0009 `config.assets`), and its contents are folded into the
managed marker — so editing a config file re-converges and restarts the units consuming it.

```
podman-host/
├── quadlets/           # one .container per service, plus monitoring.network
└── assets/
    ├── config/         # prometheus.yml, alertmanager.yml, snmp.yml, otel/tempo/loki
    │   └── rules/      # Prometheus alert rules
    └── grafana/        # dashboards + provisioning (datasources, dashboards, alerting)
```

> ⚠️ The asset renderer is **add/update-only — it never prunes**. Deleting a file here leaves
> the rendered copy on CT 4001, where the service keeps reading it. Removing an asset is a
> two-step operation: drop it from the repo **and** delete it on the host. See
> [ADR-0011](../../docs/adr/ADR-0011-alert-bus.md).

## Alerting

Prometheus rules → **Alertmanager** → receivers ([ADR-0011](../../docs/adr/ADR-0011-alert-bus.md)).
Alertmanager is the bus; Home Assistant is one receiver, not the address every producer knows.
Swapping the sink is one receiver block in `assets/config/alertmanager.yml`.

Rules live in `assets/config/rules/`. `alertname` values are load-bearing — `inhibit_rules`
match on them, so renaming an alert silently breaks an inhibition (fails *open*, never closed).

## Secrets

Declared in [`podman-host.lxc.yaml`](podman-host.lxc.yaml) under `config.secrets`, seeded as
podman secrets from the root `secrets.env` (add-only, never re-written). Non-secret config is
set as `Environment=` in the quadlets instead.

## History

This stack used to be a single Docker LXC (**CT 4000**) driven by `compose.yml` and a scripted
`deploy/install.sh`. That generation was superseded by CT 4001 (#303); its remains were deleted
once CT 4000 was destroyed. If you need it, it is in git history — not in this directory.
