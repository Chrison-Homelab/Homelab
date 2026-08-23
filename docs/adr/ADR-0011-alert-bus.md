# ADR-0011 — Alertmanager as the homelab's alert bus (Home Assistant demoted to a receiver)

- **Status:** Accepted
- **Date:** 2026-08-23
- **Deciders:** Chris
- **Relates to:** [#492](https://github.com/Chrison-Homelab/Homelab/pull/492) (UniFi monitoring,
  which first introduced Grafana-managed alerting),
  [ADR-0009 podman/quadlet migration](ADR-0009-podman-quadlet-migration.md) (how the container
  lands on CT 4001).

## Context

UniFi monitoring landed with Grafana unified alerting posting straight to a Home Assistant
webhook. That worked, but it made **Home Assistant the bus**: every producer had to know HA's
address, hold its webhook capability, and speak its shape. Swapping HA out — or adding a second
sink like email — would have meant touching every producer.

It was also not the only alert path. As of this change the homelab has **two** alert producers
and they were entirely unaware of each other:

| Producer | Alerting engine | Where it went |
|---|---|---|
| Grafana (UniFi rules) | Grafana unified alerting | HA webhook |
| Pulse (nodes, NAS, guests) | Pulse's own engine | **nowhere — no webhook configured** |

Pulse has been raising alerts about the Proxmox cluster and the NAS since it was installed and
delivering them to nobody outside its own UI.

Three properties were missing and could not be added to an HA-as-bus design at all:

- **Deduplication and grouping.** A flapping AP notifies once per evaluation.
- **Inhibition.** A dead gateway takes the APs, the switch and the WAN with it — seven
  notifications for one fault, six of which say nothing the first did not.
- **Silences.** During the VLAN migration the only way to stop the noise was to disable rules
  and remember to re-enable them.

## Decision

**Prometheus Alertmanager is the bus.** Producers send alerts to it; it decides what reaches a
human. Home Assistant is one `webhook_config` receiver at the far end.

```
Prometheus rules ─┐
Pulse webhook    ─┼─→ Alertmanager ─→ home-assistant receiver ─→ iOS
(future producer)─┘        │
                           └─→ (email / ntfy / whatever, added here alone)
```

Two supporting decisions:

- **The UniFi rules moved from Grafana to native Prometheus rules.** Grafana *can* forward its
  managed alerts to an external Alertmanager, but that leaves two Alertmanagers in the path,
  each with its own routing — a real double-notification risk and two places to look when
  something does not arrive. Prometheus rule YAML is also markedly simpler than Grafana's
  three-node query chains. Grafana is dashboards again, plus an Alertmanager **datasource** so
  its UI can browse alerts and create silences.
- **The HA webhook URL is mounted as a file** and referenced with `url_file:`, not inlined. An
  HA webhook id is a capability, so it stays a podman secret sourced from Bitwarden.

## Consequences

**Good**

- Swapping the notification sink is one receiver block. No producer changes.
- Pulse finally has somewhere to send its alerts (its webhook supports Go-templated JSON bodies
  and custom headers, so it can emit the `/api/v2/alerts` shape natively — verified, no adapter).
- Inhibition collapses a gateway outage from seven notifications to one.
- Silences during maintenance, from the Grafana UI, without editing rules.

**Costs and caveats**

- One more container, and Alertmanager is **stateful** — silences and notification state live in
  `data/alertmanager`. Losing that dir loses active silences and re-notifies everything firing.
- Alertmanager has **no native push**. It is a bus, not a replacement for HA — something still
  has to do last-mile delivery to iOS.
- `alertname` values are load-bearing: `inhibit_rules` match on them, so renaming an alert
  silently breaks an inhibition. That fails **open** (more noise), never closed.
- Grafana's previously provisioned rules and contact point had to be deleted explicitly via
  `deleteRules` / `deleteContactPoints` — removing the provisioning files alone leaves them in
  Grafana's database, still evaluating.

## Notes for whoever touches this next

The converge engine's asset renderer is **add/update-only — it does not prune**. Deleting an
asset from this repo leaves the rendered copy on the host, where Grafana keeps reading it. That
is how the retired alerting files kept re-creating the rules they were meant to remove until
they were deleted from CT 4001 by hand. Removing an asset is therefore a two-step operation
today: drop it from the repo *and* delete it on the host.
