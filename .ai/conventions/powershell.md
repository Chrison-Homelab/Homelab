
---

### `.ai/conventions/powershell.md`

```markdown
# PowerShell Conventions

## General

- Use advanced functions with `[CmdletBinding()]`.
- Always define a `param()` block.
- Support `-Verbose` and `-WhatIf` where it makes sense.

## Structure

- One primary advanced function per script file, plus helper functions as needed.
- Use `Begin`, `Process`, and `End` blocks only when streaming or pipeline behavior is required.

## Parameters

- Use clear, descriptive parameter names.
- Use `[Parameter(Mandatory = $true)]` only when truly required.
- Prefer strongly typed parameters (e.g., `[string]`, `[int]`, `[switch]`, `[hashtable]`).

## Output

- Return structured objects (`[PSCustomObject]`) instead of plain strings when possible.
- Avoid `Write-Host` in favor of:
  - `Write-Output` for pipeline output
  - `Write-Verbose` for diagnostic information
  - `Write-Error` for errors

## Error Handling

- Use `try { } catch { }` blocks for external calls and critical operations.
- Throw meaningful errors with context.
- Avoid swallowing exceptions silently.

## Style

- Avoid aliases in scripts (e.g., use `Get-ChildItem` instead of `ls`).
- Use PascalCase for function names: `Get-NodeStatus`, `Invoke-BackupJob`.
- Use comment-based help for all public functions.

## Homelab-Specific Notes

- Assume scripts may run on management workstations and remote nodes.
- Prefer remoting and APIs over direct file manipulation when appropriate.
- Keep environment-specific values configurable (e.g., via parameters or config files).
