# podman-host (CT 4001) — the Monitoring stack's rootless Podman + quadlet host

Replaces the Docker host **CT 4000**. ADR-0009 Phase 2b ([#303](https://github.com/Chrison-Homelab/Homelab/issues/303)).

- **CT 4001** — in-block (Monitoring owns 4000–4099), VLAN 1010, **DHCP** (deliberately — see below)
- **Rootless user** `podman`, subuid `10000:50000`
- **10 containers + 1 network**, all wired by name over `monitoring.network`
- Read the superproject's `docs/plans/284-podman-platform.md` **before editing a quadlet**

> ⚠️ **This host watches everything else.** Migrating it blinds monitoring for the duration —
> including the Prometheus that would otherwise tell you the migration broke something. It is
> also PowerOrchestrator's OTLP sink (#191), so that telemetry gaps until step 6 below.

## Members

| Quadlet | Container name | Why the name matters |
|---|---|---|
| `monitoring.network` | — | shared network; without it none of the by-name wiring resolves |
| `prometheus.container` | `prometheus` | `datasources.yml` → `http://prometheus:9090` |
| `snmp_exporter.container` | `snmp_exporter` | `prometheus.yml` relabels `__address__` → `snmp_exporter:9116` |
| `grafana.container` | `grafana` | — (uid 472) |
| `otel-collector.container` | `otel-collector` | scraped at `otel-collector:8889`; also the OTLP ingress |
| `tempo.container` | `tempo` | `datasources.yml` → `http://tempo:3200` (uid 10001) |
| `loki.container` | `loki` | `datasources.yml` → `http://loki:3100` (uid 10001) |
| `pulse.container` | `pulse` | — (uid 1000) |
| `exportarr-{radarr,sonarr,prowlarr}` | same | scraped at `exportarr-<app>:{9708,9709,9710}` |
| `unpoller.container` | `unpoller` | scraped at `unpoller:9130` (stateless — no data dir, no `UserNS`) |
| `alertmanager.container` | `alertmanager` | the alert bus (ADR-0011); Prometheus sends to `alertmanager:9093` (uid 65534) |
| `karma.container` | `karma` | read-only dashboard over Alertmanager on `:8080` (stateless, uid 0 — no `UserNS`) |

**Every `ContainerName=` here is load-bearing.** Compose gave `prometheus` and `snmp_exporter` no
`container_name:` at all — they resolved by *service* name — so renaming either silently breaks a
scrape or a datasource without any unit failing. Note `snmp_exporter` keeps its underscore.

## What's different from Phases 1 and 2a

### 1. It is mostly configuration → `config.assets`

Quadlets can't carry config, and this stack is config: five exporter/pipeline YAMLs plus Grafana
provisioning and dashboards. [`assets/`](assets/) is rendered to `/home/podman/monitoring/`
**before any unit starts**, and its contents are folded into the managed marker — so editing a
config re-converges and restarts the units consuming it.

This **replaced `../deploy/install.sh`** (since deleted), which shipped the same tree by
tar and then ran `docker compose up`. The assets hold the *rendered* filenames directly, dropping
that script's `sample.*.yml` → `*.yml` copy step.

### 2. Fixed-uid data dirs on 3.9 GB of state

| Dir | uid | Size | Treatment |
|---|---|---|---|
| `data/tempo` | 10001 | **3.2 G** | `keep-id:uid=10001` + one-time chown |
| `data/pulse` | 1000 | 616 M | `keep-id:uid=1000` — **already** the `podman` uid, no chown needed |
| `data/grafana` | 472 | 50 M | `keep-id:uid=472` + one-time chown |
| `data/loki` | 10001 | 68 K | `keep-id:uid=10001` + one-time chown |

**`:U` is rejected here.** It chowns *recursively on every container start* — 3.9 GB of pointless
IO per restart, growing with retention. (On the Media host's NFS mount it's worse than wasteful;
see `stacks/Media/podman-host`.)

### 3. DHCP, not the old static IP

CT 4000 pinned `10.10.0.40` and things hard-coded it — notably
`OTEL_EXPORTER_OTLP_ENDPOINT=http://10.10.0.40:4317` in `secrets.env`. The replacement is a DHCP
reservation **plus a UniFi local DNS record**, `monitoring.homelab.chrison.internal`, so nothing
points at an address again. UniFi can attach the record to the reservation itself
(`local_dns_record`), keeping name and lease defined in one place.

## Cutover runbook

```bash
# 0. provision the host, quadlets and assets
dotnet run --project Infrastructure/engine -- converge stacks/Monitoring --apply

# 1. reserve an address + register the DNS name for CT 4001 (UniFi legacy API, X-API-KEY)
#    → monitoring.homelab.chrison.internal

# 2. stop the OLD stack (CT 4000 host stays UP — the rollback path)
ssh root@hpe-01 'pct exec 4000 -- bash -lc "cd /opt/monitoring && docker compose down"'

# 3. stop the NEW units before seeding their data
ssh root@hpe-01 'pct exec 4001 -- bash -lc "cd /; U=\$(id -u podman);
  runuser -u podman -- env XDG_RUNTIME_DIR=/run/user/\$U \
    DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/\$U/bus \
    systemctl --user stop grafana tempo loki pulse prometheus otel-collector \
      snmp_exporter exportarr-radarr exportarr-sonarr exportarr-prowlarr"'

# 4. WIPE the new data dirs first. By the time you cut over, CT 4001 has been running and
#    has generated its OWN state — a plain `tar -x` would merge the old over the new and
#    leave stale files behind (e.g. a fresh grafana.db alongside migrated WAL segments).
ssh root@hpe-01 'pct exec 4001 -- rm -rf /home/podman/monitoring/data'

#    then copy 3.9 GB — streamed node-locally, no temp file (both CTs are on hpe-01)
ssh root@hpe-01 'pct exec 4000 -- tar -C /opt/monitoring -cf - data \
  | pct exec 4001 -- tar -C /home/podman/monitoring -xf -'

#    every container either uses keep-id (mapping its uid to `podman`) or runs as root
#    (which the default mapping sends to `podman`), so ONE uniform chown is correct.
ssh root@hpe-01 'pct exec 4001 -- chown -R podman:podman /home/podman/monitoring'

# 5. start the units, then verify (see below)

# 6. repoint the OTLP endpoint at the NAME, not an address
#    secrets.env.template → OTEL_EXPORTER_OTLP_ENDPOINT=http://monitoring.homelab.chrison.internal:4317
#    scripts/secrets-sync.sh, then redeploy PowerOrchestrator so it picks the new endpoint up
#    …and grep the repo for any other hard-coded 10.10.0.40

# 7. leave CT 4000 STOPPED but NOT destroyed
ssh root@hpe-01 'pct stop 4000'
```

### Verify, in this order

1. All 10 units active; `podman ps` shows 10 containers
2. **Inter-container DNS** — `getent hosts prometheus` etc. from inside a container
3. **Prometheus targets all UP** (`/api/v1/targets`) — this is the real test of the name wiring
4. **Grafana** loads with its migrated dashboards and all three datasources resolving
5. **Pulse** still knows its nodes/agents (its state carries admin creds + API token)
6. **Tempo/Loki** return pre-migration data — proves the 3.9 GB landed with correct ownership
7. Units survive an LXC reboot

**Rollback:** was `pct start 4000` then `docker compose up -d` inside it. **No longer available**
— CT 4000 was destroyed and its shape and compose files deleted. Rolling back now means restoring
from git history, not starting a stopped guest.

## Follow-up once CT 4000 is destroyed — DONE

CT 4000 is gone from all three nodes, so the dead weight this section listed was deleted:
`../compose.yml`, `../deploy/`, `../config/sample.*.yml`, `../grafana/**`,
`../secrets.env.local.template` and the CT 4000 shape.

They had stopped being merely redundant. `config/sample.prometheus.yml` had drifted **41 lines**
behind `assets/config/prometheus.yml` (no `unifi` job, no alerting, no `rule_files`), and the old
`grafana/provisioning/datasources.yml` had no Alertmanager entry — while the stack README still
told you to `cp sample.prometheus.yml prometheus.yml`. Following those instructions would have
deployed a config missing most of the current setup.
