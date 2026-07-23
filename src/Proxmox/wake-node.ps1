#!/usr/bin/env pwsh
# wake-node.ps1
#
# PowerShell version of wake-node.sh.
# Sends a Wake-on-LAN magic packet to bring a sleeping Proxmox node back up.
# Designed to run from an always-on node (e.g. nuc-01/hpe-01) so heavy nodes
# (desktop-01, hpe-02) can be powered down when idle and woken on demand.
#
# The target NIC must have WoL armed (see wol-arm.service / `ethtool -s <if> wol g`)
# and BIOS WoL enabled. Sender and target must share an L2 broadcast domain, OR
# you must pass a directed subnet broadcast via -Broadcast.
#
# Usage:
#   pwsh ./wake-node.ps1 desktop-01                 # known node (see $NodeMacs below)
#   pwsh ./wake-node.ps1 18:c0:4d:de:9f:82          # raw MAC
#   pwsh ./wake-node.ps1 desktop-01 -Broadcast 192.168.179.255   # directed broadcast
#   pwsh ./wake-node.ps1 desktop-01 -Port 7         # custom UDP port (default 9)
#
# Remote one-liner:
#   pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/wake-node.ps1' -UseBasicParsing).Content" -- desktop-01
#
# Requirements: PowerShell Core (uses .NET UdpClient — no extra packages).

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Target,
    [string]$Broadcast = "255.255.255.255",
    [int]$Port = 9
)

$ErrorActionPreference = "Stop"

# Known node → MAC registry. Add nodes here as WoL is armed on them.
$NodeMacs = @{
    "desktop-01" = "18:c0:4d:de:9f:82"
    "hpe-01"     = "c8:d3:ff:9d:da:02"
    "nuc-01"     = "b8:ae:ed:72:82:fe"
}

# Resolve a known node name to its MAC; otherwise treat the argument as a MAC.
$mac = if ($NodeMacs.ContainsKey($Target)) { $NodeMacs[$Target] } else { $Target }

# Normalise + validate MAC (accept : or - separators).
$mac = $mac -replace '-', ':'
if ($mac -notmatch '^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$') {
    Write-Error "'$Target' is not a known node and not a valid MAC (aa:bb:cc:dd:ee:ff). Known nodes: $($NodeMacs.Keys -join ', ')"
    exit 1
}

Write-Host "Waking $Target ($mac) via ${Broadcast}:${Port} ..." -ForegroundColor Green

# Build the magic packet: 6x 0xFF followed by the MAC repeated 16 times.
$macBytes = $mac.Split(':') | ForEach-Object { [Convert]::ToByte($_, 16) }
$packet = [byte[]]@(, [byte]0xFF * 6) + ($macBytes * 16)

$udp = New-Object System.Net.Sockets.UdpClient
try {
    $udp.EnableBroadcast = $true
    [void]$udp.Send($packet, $packet.Length, $Broadcast, $Port)
    Write-Host "Magic packet sent." -ForegroundColor Green
}
finally {
    $udp.Close()
}

Write-Host "Done. Give the node ~30-60s to POST and rejoin the cluster."
