#!/usr/bin/env pwsh
# healthcheck.ps1 — post-outage / anytime homelab health check (PowerShell twin
# of healthcheck.sh; keep the two in sync).
#
# Probes every device in healthcheck.hosts (ICMP + key TCP ports) and prints a
# green/red table. No secrets, no .NET toolchain beyond PowerShell Core itself.
#
# Usage:
#   ./scripts/healthcheck.ps1                 # probe everything
#   ./scripts/healthcheck.ps1 -Public         # also check https://proxmox.chrison.dev
#   ./scripts/healthcheck.ps1 -Hosts FILE     # use a different inventory file
#   ./scripts/healthcheck.ps1 -Quiet          # table only, no hints
#
# Exit codes: 0 = all critical hosts up · 1 = a critical host is down.
#
# Inventory lives in healthcheck.hosts. Waking a downed node is a separate,
# deliberate step: src/Proxmox/wake-node.ps1 <node>.

[CmdletBinding()]
param(
    [switch]$Public,
    [string]$Hosts,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$HostsFile  = if ($Hosts) { $Hosts } else { Join-Path $ScriptDir 'healthcheck.hosts' }
$PublicUrl  = 'https://proxmox.chrison.dev/api2/json/version'
$PingTimeout = 2      # seconds
$TcpTimeout  = 2000   # milliseconds

if (-not (Test-Path $HostsFile)) { Write-Error "Inventory not found: $HostsFile"; exit 2 }

function Test-Ping([string]$Ip) {
    try { return [bool](Test-Connection -TargetName $Ip -Count 1 -TimeoutSeconds $PingTimeout -Quiet -ErrorAction Stop) }
    catch { return $false }
}

function Test-TcpPort([string]$Ip, [int]$Port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $iar = $client.BeginConnect($Ip, $Port, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne($TcpTimeout, $false) -and $client.Connected) {
            $client.EndConnect($iar); return $true
        }
        return $false
    } catch { return $false }
    finally { $client.Close() }
}

Write-Host ("`n{0} Homelab health check " -f [System.Char]::ConvertFromUtf32(0x1FA7A)) -NoNewline -ForegroundColor White
Write-Host ("({0})" -f [System.Net.Dns]::GetHostName()) -ForegroundColor DarkGray
Write-Host "`n  STATUS   HOST            IP                ROLE      DETAIL" -ForegroundColor DarkGray

$critDown = 0; $warnDown = 0; $totalUp = 0; $total = 0

foreach ($line in Get-Content $HostsFile) {
    $t = $line.Trim()
    if ($t -eq '' -or $t.StartsWith('#')) { continue }
    $f = $t -split '\s+'
    if ($f.Count -lt 5) { continue }
    $name, $ip, $role, $ports, $severity = $f[0], $f[1], $f[2], $f[3], $f[4]
    $total++

    $icmp = Test-Ping $ip

    $detail = ''; $anyPortUp = $false; $hadPorts = $false
    if ($ports -ne '-') {
        $hadPorts = $true
        foreach ($p in ($ports -split ',')) {
            if (Test-TcpPort $ip ([int]$p)) { $detail += "$p up  "; $anyPortUp = $true }
            else { $detail += "$p down  " }
        }
    }
    $detail += if ($icmp) { 'ping' } else { 'no-ping' }

    $up = ($hadPorts -and $anyPortUp) -or ((-not $hadPorts) -and $icmp)

    if ($up) { $icon = '🟢 UP  '; $colour = 'Green'; $totalUp++ }
    elseif ($severity -eq 'optional') { $icon = '🟡 WARN'; $colour = 'Yellow'; $warnDown++ }
    else { $icon = '🔴 DOWN'; $colour = 'Red'; $critDown++ }

    Write-Host ("{0}  {1,-14}  {2,-15}   {3,-8}  {4}" -f $icon, $name, $ip, $role, $detail) -ForegroundColor $colour
}

if ($Public) {
    $code = 0
    try {
        $resp = Invoke-WebRequest -Uri $PublicUrl -TimeoutSec 8 -SkipCertificateCheck -SkipHttpErrorCheck
        $code = [int]$resp.StatusCode
    } catch { $code = 0 }
    if ($code -eq 200 -or $code -eq 401) {
        Write-Host ("{0}  {1,-14}  {2,-15}   {3,-8}  HTTP {4}" -f '🟢 UP  ', 'public', 'proxmox.chrison.dev', 'ingress', $code) -ForegroundColor Green
    } else {
        Write-Host ("{0}  {1,-14}  {2,-15}   {3,-8}  HTTP {4} (tunnel/origin down)" -f '🔴 DOWN', 'public', 'proxmox.chrison.dev', 'ingress', $code) -ForegroundColor Red
    }
}

Write-Host ''
if ($critDown -eq 0 -and $warnDown -eq 0) {
    Write-Host ("✅ All {0} hosts up." -f $total) -ForegroundColor Green
} elseif ($critDown -eq 0) {
    Write-Host ("✅ All critical hosts up — {0} optional host(s) down." -f $warnDown) -ForegroundColor Green
} else {
    Write-Host ("🔴 {0} critical host(s) DOWN ({1}/{2} up)." -f $critDown, $totalUp, $total) -ForegroundColor Red
    if (-not $Quiet) {
        Write-Host "`nNext steps:" -ForegroundColor White
        Write-Host "  * Node powered but unreachable -> likely BIOS/POST hang; needs a monitor+keyboard."
        Write-Host "  * Node fully off with WoL armed -> wake from an up node:"
        Write-Host "      ssh root@<up-node> 'bash src/Proxmox/wake-node.sh <down-node>'"
        Write-Host "  * NAS down -> check DS1813 front panel / PSU; NFS mounts on nodes depend on it."
    }
}

if ($critDown -gt 0) { exit 1 } else { exit 0 }
