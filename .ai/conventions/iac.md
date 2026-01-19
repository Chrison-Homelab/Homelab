# Infrastructure-as-Code Conventions

## General

- Prefer declarative definitions over imperative logic.
- Keep environment-specific values in variables, tfvars, inventory, or separate config files.
- Avoid embedding secrets directly in code; use secret managers or environment-specific secret stores.

## Naming

- Use consistent naming patterns, for example:
  - `<service>-<role>-<env>` (e.g., `nfs-storage-prod`, `proxmox-node-lab`)
- Use lowercase with hyphens for resource names where allowed.

## Structure

- Group related resources into modules/roles.
- Keep modules small and focused.
- Document module inputs, outputs, and assumptions.

## Version Control

- Commit IaC definitions and module code.
- Do not commit generated state files or secrets.
- Use `.gitignore` to exclude state, cache, and local artifacts.

## Homelab-Specific Notes

- Reflect the actual topology described in `context/homelab-architecture.md` and `context/network-layout.md`.
- For storage and mounts, align with `context/storage-layout.md`.
- When in doubt, favor clarity and maintainability over cleverness.
