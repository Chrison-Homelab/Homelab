# `.ai` – Homelab AI Agent Instructions

This directory contains instructions, conventions, and context for AI agents working in this repository.

## Structure

- `agent.md`  
  Core instructions for how an AI should behave when generating or modifying code and configuration.

- `conventions/`  
  Language- and domain-specific rules:
  - `bash.md` – Bash scripting conventions
  - `powershell.md` – PowerShell scripting conventions
  - `iac.md` – Infrastructure-as-code conventions
  - `documentation.md` – Documentation style and expectations

- `patterns/`  
  Reusable templates and patterns:
  - `script-template-bash.md`
  - `script-template-powershell.md`
  - `module-template.md`
  - `troubleshooting-patterns.md`

- `context/`  
  Homelab-specific architectural context:
  - `homelab-architecture.md`
  - `network-layout.md`
  - `storage-layout.md`
  - `automation-philosophy.md`

AI agents should treat this directory as the source of truth for style, structure, and architectural intent.
