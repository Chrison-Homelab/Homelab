# Monitoring

## Synology NAS

### Synology NAS: Setup

**Enable SNMP on DSM:**

1. Control Panel → Terminal & SNMP → SNMP → Enable SNMP v2c
2. Set a community string (e.g., public or your own)

## Config files

`config/*` is gitignored except `config/sample.*` — copy each sample to its real name (which
`compose.yml` mounts) and customise:

```bash
cd stacks/monitoring/config
cp sample.prometheus.yml      prometheus.yml
cp sample.snmp.yml            snmp.yml
# OpenTelemetry pipeline (PowerOrchestrator / #191):
cp sample.otel-collector.yml  otel-collector.yml
cp sample.tempo.yml           tempo.yml
cp sample.loki-config.yml     loki-config.yml
```

## OpenTelemetry pipeline

`otel-collector` receives OTLP (gRPC :4317 / HTTP :4318) and fans out: **metrics → Prometheus**
(scraped off the collector's :8889; see the `otel-collector` job in `sample.prometheus.yml`),
**traces → Tempo**, **logs → Loki**. Grafana is pre-provisioned with all three datasources and a
**Node Power Orchestrator** dashboard. Point an emitter at the collector with
`OTEL_EXPORTER_OTLP_ENDPOINT=http://<this-host>:4317`.

```bash
docker compose up -d otel-collector tempo loki prometheus grafana
```
