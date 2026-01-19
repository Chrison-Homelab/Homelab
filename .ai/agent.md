# Homelab AI Agent Instructions

## Purpose

This repository contains modular automation and infrastructure definitions for a multi-node homelab environment.  
As an AI agent, your role is to generate and modify scripts, infrastructure-as-code, and documentation that:

- respect the conventions in `.ai/conventions/`
- align with the patterns in `.ai/patterns/`
- are consistent with the architecture in `.ai/context/`

## Core Principles

- Prefer **modular**, **composable** design.
- Maintain **idempotency** wherever possible.
- Avoid hardcoded values; prefer parameters, configuration files, or environment discovery.
- Default to **safe**, **reversible** operations.
- Assume **multi-node**, **multi-NAS**, containerized workloads.
- Make minimal assumptions about the runtime environment; document any assumptions you do make.

## When Generating or Editing Scripts

- Follow:
  - `conventions/bash.md` for Bash
  - `conventions/powershell.md` for PowerShell
- Use the templates in `patterns/script-template-bash.md` and `patterns/script-template-powershell.md` as a baseline.
- Include:
  - clear parameter handling
  - help/usage output
  - logging with context
  - robust error handling
  - dry-run or `WhatIf` support where appropriate
- Prefer functions over inline logic; keep the top-level script flow readable.
- Avoid introducing global state unless necessary; prefer passing parameters.

## When Generating or Editing Infrastructure-as-Code

- Follow `conventions/iac.md`.
- Keep resources **declarative** and **environment-agnostic** where possible.
- Avoid provider-specific lock-in unless explicitly justified and documented.
- Use variables/parameters for environment-specific values.
- Document architectural decisions inline or in `docs/` using the documentation conventions.

## When Generating or Editing Documentation

- Follow `conventions/documentation.md`.
- Keep documentation close to the code (e.g., `docs/`, inline help, or README files).
- Document:
  - purpose
  - inputs/outputs
  - assumptions
  - side effects
  - failure modes and troubleshooting hints

## Using Context

Before making non-trivial changes, consult:

- `context/homelab-architecture.md`
- `context/network-layout.md`
- `context/storage-layout.md`
- `context/automation-philosophy.md`

Use this context to:

- choose appropriate patterns (e.g., NFS bind-mounts, FS-Cache, container layout)
- avoid breaking existing assumptions
- keep new components consistent with the homelab’s overall design philosophy

## Safety and Caution

- Never assume destructive operations are acceptable by default.
- For operations that modify or delete data, require explicit confirmation (e.g., flags) and document the impact.
- Prefer read-only or dry-run modes when designing new tooling.
