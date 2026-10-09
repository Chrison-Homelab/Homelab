# Upgrading a pinned container image

Stateful services are pinned to an explicit version tag instead of `:latest` +
`AutoUpdate=registry`. A version bump is a deliberate change that goes through this
process, never something the registry does to us overnight.

## Why

Tempo (#599) is the case that made this the rule. It ran `:latest` with auto-update, and:

- Grafana stopped moving `latest` at a 3.0.0 build on 2 June 2026, so "auto-update" was a
  silent no-op for four months while 3.0.1–3.1.0 shipped.
- The config still described the 2.x architecture (a `compactor:` that 3.0 removed).
- One zero-byte block from 12 July failed every blocklist poll, so compaction and retention
  stopped for months. Ingest and queries kept working, so nothing looked wrong.

Nothing about this failed loudly. An unpinned image tells you neither what you are running
nor when that changed.

## The process

1. **Find what is running now**: `podman inspect <ctr> --format '{{.ImageName}}'` and the
   binary's own `--version` (or `/api/status/buildinfo`). Do not assume the tag tells you.
2. **Read the release notes for every version between current and target**, not just the
   target. Look for: breaking changes, removed or renamed config keys, changed defaults,
   on-disk format changes, and fixes in the areas you depend on.
3. **Check on-disk state against the new version's requirements** (e.g. Tempo 3.1 refuses
   vParquet3 blocks; Prometheus/Loki have their own schema/format gates).
4. **Adapt the config** in the same PR as the tag bump. Prefer pinning a changed default
   to its old value first and moving it as a separate change: one variable at a time.
5. **Take a backup** if the service's data matters: `vzdump <ctid> --storage pbs-homelab
   --mode snapshot` from the Proxmox node.
6. **Converge**, then **verify the service is doing its job**, not merely running: read the
   effective config (`/status/config` or equivalent), and check the background work
   (compaction, retention, rule evaluation) shows progress in its metrics.
7. Record anything surprising in the quadlet's comment above `Image=`.

## Which images are pinned

Tracked in the image-pinning milestone. Stateless, easily replaced containers may stay on
`:latest` with auto-update; anything holding data or with a config schema that changes
between majors gets pinned.
