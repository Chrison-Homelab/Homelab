# Infrastructure

The **new world**: C#-native, reproducible provisioning for the homelab hub.
See [ADR-0001](../docs/adr/ADR-0001-iac-tooling.md) and the
[plan](../docs/plans/iac-csharp-native.md).

> `/Infrastructure` (this) is the new C#-native provisioning world.
> `/infra` is the **legacy** world (Ansible / OpenTofu / docker monitoring) —
> frozen, not extended.

## How it works

- **Submodules declare shape.** A `Homelab.Stacks.*` repo carries a `shape.yaml`
  describing what it needs (an LXC of a given size, VLAN, mounts, …). It never
  runs provisioning itself.
- **The hub owns the engine** (C#, built on ProxmoxSharp / SynoSharp). It reads
  shapes, discovers live state, plans a diff, and converges.
- **Shape is just data** — declarative YAML validated against [`schema/`](schema).
  It can be swapped for another format later without touching the engine.

## Layout

```
Infrastructure/
├── schema/
│   └── shape.schema.json     # the contract every shape.yaml validates against
├── nodes/                    # hub-owned desired state for the bare hosts/NAS
│   ├── nuc-01.yaml           #   (specs TODO — confirmed by Phase 2 discovery)
│   ├── hpe-01.yaml
│   └── desktop-01.yaml
└── examples/
    └── servarr.lxc.yaml      # reference shape until submodule shapes are authored
```

The C# engine itself lands in Phase 2/3 (see the plan) — Phase 1 is **Define**:
establish the shape contract before we write any provisioning code.

## Status

Phase 1 — **Define**. Discovery (read-only, via ProxmoxSharp) and reproducible
converge come next. Shapes here are authored against *current* knowledge and will
be reconciled against the live cluster during discovery, not trusted blindly.
