#!/usr/bin/env pwsh
#
# secrets-bootstrap.ps1 — first-time setup for a Windows machine so it can
# regenerate secrets.env from Bitwarden Secrets Manager. Idempotent: skips any
# step already done. Re-run with -Force to re-enter the access token.
#
# From the repo root:  ./scripts/secrets-bootstrap.ps1
#
# It will (only what's missing):
#   1. install the bws CLI (official Bitwarden release) into ~/.local/bin + PATH
#   2. pin the EU region (bws config server-base https://vault.bitwarden.eu)
#   3. store your SM access token, DPAPI-encrypted (per-user), at
#      ~/.config/bws/access-token.dpapi
#   4. run secrets-sync.ps1 to write secrets.env
[CmdletBinding()]
param(
  [string]$BwsVersion = '2.1.0',
  [switch]$Force
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$BinDir   = Join-Path $HOME '.local/bin'
$TokFile  = Join-Path $HOME '.config/bws/access-token.dpapi'

# ── 1. install bws if missing ──
if (-not (Get-Command bws -ErrorAction SilentlyContinue)) {
  $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'aarch64' } else { 'x86_64' }
  $asset = "bws-$arch-pc-windows-msvc-$BwsVersion.zip"
  $url   = "https://github.com/bitwarden/sdk-sm/releases/download/bws-v$BwsVersion/$asset"
  Write-Host "Installing bws $BwsVersion ($arch) -> $BinDir"
  New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
  $zip = Join-Path $env:TEMP $asset
  Invoke-WebRequest -Uri $url -OutFile $zip
  Expand-Archive -Path $zip -DestinationPath (Join-Path $env:TEMP 'bws-extract') -Force
  Copy-Item (Join-Path $env:TEMP 'bws-extract/bws.exe') (Join-Path $BinDir 'bws.exe') -Force
  Remove-Item $zip, (Join-Path $env:TEMP 'bws-extract') -Recurse -Force -ErrorAction SilentlyContinue
  # add to PATH (session + persistent user PATH)
  if ($env:Path -notlike "*$BinDir*") { $env:Path = "$BinDir;$env:Path" }
  $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
  if ($userPath -notlike "*$BinDir*") {
    [Environment]::SetEnvironmentVariable('Path', "$BinDir;$userPath", 'User')
    Write-Host "Added $BinDir to your user PATH (new terminals pick it up)."
  }
} else {
  Write-Host "bws already installed: $((bws --version) 2>&1)"
}

# ── 2. pin EU region ──
bws config server-base https://vault.bitwarden.eu | Out-Null
Write-Host "bws region pinned -> https://vault.bitwarden.eu"

# ── 3. store the access token (DPAPI, per-user) ──
if ($Force -or -not (Test-Path -LiteralPath $TokFile)) {
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TokFile) | Out-Null
  Write-Host "Paste your SM access token (from Bitwarden item 'BW Secrets Manager - Github')."
  $sec = Read-Host -AsSecureString 'BWS_ACCESS_TOKEN'
  $sec | ConvertFrom-SecureString | Set-Content -LiteralPath $TokFile -NoNewline
  Write-Host "Token stored (DPAPI, this user only) -> $TokFile"
} else {
  Write-Host "Access token already stored -> $TokFile (use -Force to replace)"
}

# ── 4. generate secrets.env ──
Write-Host "`nGenerating secrets.env ..."
& (Join-Path $RepoRoot 'scripts/secrets-sync.ps1')
