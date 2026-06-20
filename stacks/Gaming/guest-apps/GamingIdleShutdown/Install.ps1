<#
  Install.ps1 — register GamingIdleShutdown to autostart in the current user's
  session (no elevation needed) and launch it. Run in the Windows guest (1002),
  signed in as the gamer, from the published output folder.
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'GamingIdleShutdown.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    throw "GamingIdleShutdown.exe not found at '$ExePath'. Publish first (see README.md) and run this from the publish folder."
}
$ExePath = (Resolve-Path $ExePath).Path

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $run -Name 'GamingIdleShutdown' -Value "`"$ExePath`"" -PropertyType String -Force | Out-Null
Write-Host "Registered autostart (HKCU\...\Run\GamingIdleShutdown -> $ExePath)"

# Stop any running instance, then (re)launch.
Get-Process GamingIdleShutdown -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process $ExePath
Write-Host "Started."
Write-Host "Log:    $env:LOCALAPPDATA\GamingIdleShutdown\log.txt"
Write-Host "Config: config.json beside the exe (optional). dryRun defaults to TRUE —"
Write-Host "        watch the log for a few days, then set dryRun=false to arm it."
