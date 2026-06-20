<#
  Install.ps1 - install GamingIdleShutdown for the CURRENT user and launch it.
  Run inside the Windows guest (1002), signed in as the gamer (no elevation needed).

  Works from any folder (incl. a copied download): it COPIES the app to a persistent
  local path, registers logon autostart (HKCU Run), and starts it.

  MUST run in the gamer's interactive session - the app reads per-session input-idle
  (GetLastInputInfo), so running it as SYSTEM/a service would misread idle.
#>
param(
    [string]$Source = $PSScriptRoot,
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'GamingIdleShutdown')
)
$ErrorActionPreference = 'Stop'

$exeName = 'GamingIdleShutdown.exe'
$srcExe = Join-Path $Source $exeName
if (-not (Test-Path $srcExe)) {
    throw "$exeName not found in '$Source'. Copy the published folder here and re-run."
}

# Stop any running instance and WAIT for it to release the exe before copying.
Get-Process GamingIdleShutdown -ErrorAction SilentlyContinue | Stop-Process -Force
for ($i = 0; $i -lt 20 -and (Get-Process GamingIdleShutdown -ErrorAction SilentlyContinue); $i++) {
    Start-Sleep -Milliseconds 250
}
New-Item -ItemType Directory -Path $Target -Force | Out-Null
Copy-Item $srcExe (Join-Path $Target $exeName) -Force
$srcCfg = Join-Path $Source 'config.json'
if ((Test-Path $srcCfg) -and -not (Test-Path (Join-Path $Target 'config.json'))) {
    Copy-Item $srcCfg (Join-Path $Target 'config.json') -Force   # never clobber an existing config
}
$tgtExe = Join-Path $Target $exeName

# Logon autostart for THIS user.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $run -Name 'GamingIdleShutdown' -Value ('"' + $tgtExe + '"') -PropertyType String -Force | Out-Null

Start-Process $tgtExe
Write-Host "Installed to $Target and started; autostart registered for $env:USERNAME."
Write-Host "Log: $env:LOCALAPPDATA\GamingIdleShutdown\log.txt"
Write-Host 'dryRun defaults true - set dryRun=false in config.json next to the exe to arm it.'
