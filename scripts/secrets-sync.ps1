#!/usr/bin/env pwsh
#
# secrets-sync.ps1 — regenerate secrets.env from secrets.env.template, filling the
# blank (secret) keys from Bitwarden Secrets Manager (project "Homelab").
# PowerShell twin of secrets-sync.sh — produces a byte-identical, bash-sourceable
# secrets.env (LF line endings, single-quoted values).
#
#   • Reads FROM Secrets Manager only (bws + stored token) — no vault unlock.
#   • Non-secret template lines pass through verbatim; blank keys are filled.
#   • Any blank template key NOT found in SM is left blank and reported LOUDLY.
#   • No secret value is ever printed.
#
# Access token resolution (first hit wins):
#   1. $env:BWS_ACCESS_TOKEN
#   2. DPAPI-encrypted file  ~/.config/bws/access-token.dpapi   (Windows, per-user)
#      Create it once with:
#        Read-Host 'BWS token' -AsSecureString | ConvertFrom-SecureString |
#          Set-Content $HOME/.config/bws/access-token.dpapi
#   (On macOS the .sh script reads the token from Keychain instead.)
#
# Usage:
#   ./scripts/secrets-sync.ps1                        # writes ./secrets.env
#   ./scripts/secrets-sync.ps1 /tmp/out               # custom output path (for testing)
#   ./scripts/secrets-sync.ps1 <out> <template>       # custom output AND template (other stacks)
[CmdletBinding()]
param([string]$OutPath, [string]$TemplatePath)

$ErrorActionPreference = 'Stop'
$ProjectId = 'ceb88092-7a26-4882-9e7b-b48a000a8f9a'   # SM "Homelab" project
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$Template  = if ($TemplatePath) { $TemplatePath } else { Join-Path $RepoRoot 'secrets.env.template' }
if (-not $OutPath) { $OutPath = Join-Path $RepoRoot 'secrets.env' }

if (-not (Test-Path -LiteralPath $Template)) { throw "template not found: $Template" }

# ── access token ──
$token = $env:BWS_ACCESS_TOKEN
if (-not $token) {
  $tokFile = Join-Path $HOME '.config/bws/access-token.dpapi'
  if (Test-Path -LiteralPath $tokFile) {
    $sec   = Get-Content -LiteralPath $tokFile | ConvertTo-SecureString
    $token = [System.Net.NetworkCredential]::new('', $sec).Password
  }
}
if (-not $token) { throw "no BWS_ACCESS_TOKEN (env var or ~/.config/bws/access-token.dpapi)" }
$env:BWS_ACCESS_TOKEN = $token
if (-not $env:BWS_SERVER_URL) { $env:BWS_SERVER_URL = 'https://vault.bitwarden.eu' }

# ── pull all SM secrets once ──
$sm = @{}
foreach ($s in (bws secret list $ProjectId -o json | ConvertFrom-Json)) { $sm[$s.key] = $s.value }

# single-quote a value for safe `set -a; . secrets.env` sourcing (bash rules)
function Quote-Sh([string]$v) { "'" + ($v -replace "'", "'\''") + "'" }

$filled = 0; $pass = 0; $missing = @()
$out = New-Object System.Collections.Generic.List[string]
foreach ($line in (Get-Content -LiteralPath $Template)) {
  if ($line -match '^(\s*)([A-Za-z_][A-Za-z0-9_]*)=$') {
    $indent = $Matches[1]; $key = $Matches[2]
    if ($sm.ContainsKey($key)) { $out.Add("$indent$key=$(Quote-Sh ([string]$sm[$key]))"); $filled++ }
    else { $out.Add($line); $missing += $key }
  } else { $out.Add($line); $pass++ }
}

# write LF-terminated, UTF-8 no BOM (matches the .sh output)
$text = ($out -join "`n") + "`n"
[System.IO.File]::WriteAllText($OutPath, $text, (New-Object System.Text.UTF8Encoding($false)))

# best-effort restrictive perms
try {
  if ($IsWindows) {
    icacls $OutPath /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
  } else { & chmod 600 $OutPath }
} catch { }

Write-Host "secrets.env written -> $OutPath"
Write-Host "  filled from SM: $filled   passthrough lines: $pass"
if ($missing.Count -gt 0) {
  Write-Warning ("MISSING from SM (left blank): " + ($missing -join ' '))
  Write-Warning "  -> add them to the SM 'Homelab' project, then re-run."
  exit 2
}
