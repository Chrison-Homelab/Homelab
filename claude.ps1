#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Launch Claude Code with MCP secrets loaded from .mcp.env.

.DESCRIPTION
    Reads .mcp.env (bash `export VAR=value` syntax) from the repo root, sets each
    variable in THIS process's environment only (never persisted to the user/registry,
    so secrets don't leak), then launches `claude`, passing through any arguments.

    The MCP servers in .mcp.json reference these vars via ${...} interpolation, so
    they must be present in the environment Claude Code is launched from.

.EXAMPLE
    .\claude.ps1
    .\claude.ps1 --resume
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ClaudeArgs
)

$ErrorActionPreference = 'Stop'

# Rebuild PATH from the registry so tools installed since this terminal opened
# (e.g. uv/uvx via winget, needed by the unifi MCP server) are visible to Claude
# Code and the MCP servers it spawns — without having to open a fresh terminal.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path', 'User')

$envFile = Join-Path $PSScriptRoot '.mcp.env'
if (-not (Test-Path $envFile)) {
    Write-Error ".mcp.env not found at $envFile. Copy .mcp.env.example to .mcp.env and fill in your secrets."
    exit 1
}

$loaded = @()
$empty = @()

foreach ($line in Get-Content $envFile) {
    # Skip blanks and comment-only lines.
    if ($line -match '^\s*(#|$)') { continue }

    # Match: optional `export`, NAME, `=`, then the rest.
    if ($line -notmatch '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { continue }

    $name = $matches[1]
    $rest = $matches[2]

    # Value may be double-quoted, single-quoted, or a bareword (with an optional
    # trailing `# comment`). Quotes preserve any inline `#`.
    if ($rest -match '^"([^"]*)"') {
        $value = $matches[1]
    }
    elseif ($rest -match "^'([^']*)'") {
        $value = $matches[1]
    }
    else {
        $value = ($rest -replace '\s+#.*$', '').Trim()
    }

    if ([string]::IsNullOrEmpty($value)) {
        $empty += $name
        continue
    }

    # Process scope: inherited by the launched `claude` child, gone when this shell exits.
    Set-Item -Path "Env:$name" -Value $value
    $loaded += $name
}

if ($loaded.Count -gt 0) {
    Write-Host "Loaded $($loaded.Count) var(s) from .mcp.env: $($loaded -join ', ')" -ForegroundColor Green
}
if ($empty.Count -gt 0) {
    Write-Host "Skipped empty var(s): $($empty -join ', ')" -ForegroundColor Yellow
}

$claude = Get-Command claude -ErrorAction SilentlyContinue
if (-not $claude) {
    Write-Error "'claude' was not found on PATH. Install Claude Code or add it to PATH."
    exit 1
}

& $claude.Source @ClaudeArgs
exit $LASTEXITCODE
