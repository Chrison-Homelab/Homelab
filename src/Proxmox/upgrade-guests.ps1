#!/usr/bin/env pwsh
# upgrade-guests.ps1 — PowerShell twin of upgrade-guests.sh (kept functionally in sync).
# Apply pending OS package upgrades inside every RUNNING LXC on this Proxmox node.
#
#   pwsh ./upgrade-guests.ps1                 # upgrade all running guests
#   pwsh ./upgrade-guests.ps1 -DryRun         # report pending/security/reboot-required only
#   pwsh ./upgrade-guests.ps1 -Reboot         # reboot guests that flag reboot-required afterwards
#   pwsh ./upgrade-guests.ps1 -Only 5008,8000
#
# OS packages only (apt). Apps, container images and the node itself update through their own,
# separate paths — see the .sh header and #436.
param(
    [switch]$DryRun,
    [switch]$Reboot,
    [int[]]$Only = @()
)
$ErrorActionPreference = 'Stop'

$ids = (& pct list | Select-Object -Skip 1 | ForEach-Object { $f = ($_ -split '\s+'); if ($f[1] -eq 'running') { [int]$f[0] } })
if ($Only.Count -gt 0) { $ids = $Only }

$report = @'
apt-get update -qq >/dev/null 2>&1
up=$(apt list --upgradable 2>/dev/null | grep -c "/"); sec=$(apt list --upgradable 2>/dev/null | grep -ci security)
rb=""; [ -f /var/run/reboot-required ] && rb="reboot-required"
echo "$up $sec $rb"
'@
$upgrade = @'
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null 2>&1
before=$(apt list --upgradable 2>/dev/null | grep -c "/"); sec=$(apt list --upgradable 2>/dev/null | grep -ci security)
apt-get -qq -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold dist-upgrade >/var/log/homelab-upgrade.log 2>&1; rc=$?
apt-get -qq -y autoremove >>/var/log/homelab-upgrade.log 2>&1 || true
left=$(apt list --upgradable 2>/dev/null | grep -c "/")
rb=""; [ -f /var/run/reboot-required ] && rb="reboot-required"
st="upgraded"; [ $rc -ne 0 ] && st="FAILED(rc=$rc)"; [ "$left" != 0 ] && st="$st left=$left"
echo "$before $sec $st $rb"
'@

'{0,-6} {1,-24} {2,-8} {3,-9} {4}' -f 'CT','NAME','PENDING','SECURITY','STATE'
$total = 0; $secTotal = 0; $rebooters = @()
foreach ($id in $ids) {
    $name = ((& pct config $id | Select-String '^hostname:') -split '\s+')[1]
    $script = if ($DryRun) { $report } else { $upgrade }
    $out = (& pct exec $id -- bash -c $script 2>$null | Select-Object -Last 1)
    if (-not $out) { $out = '? ? exec-failed' }
    $parts = $out -split ' ', 3
    '{0,-6} {1,-24} {2,-8} {3,-9} {4}' -f $id, $name, $parts[0], $parts[1], $parts[2]
    if ($parts[0] -match '^\d+$') { $total += [int]$parts[0]; $secTotal += [int]$parts[1] }
    if ($parts[2] -like '*reboot-required*') { $rebooters += $id }
}
"---- $($ids.Count) guest(s): $total pending ($secTotal security) $(if ($DryRun) {'reported'} else {'processed'})"
if ($rebooters.Count -gt 0) {
    "reboot required: $($rebooters -join ' ')"
    if ($Reboot -and -not $DryRun) { foreach ($id in $rebooters) { "rebooting CT $id"; & pct reboot $id } }
}
