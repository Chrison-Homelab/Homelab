#!/usr/bin/env pwsh
# install-pulse-agent.ps1
#
# PowerShell version of install-pulse-agent.sh.
# Installs, updates or removes the Pulse unified agent on a Proxmox VE node.
#
# The Proxmox API alone already gives Pulse the guest inventory, cluster state and
# storage/backup view. The agent exists for what the API physically cannot return:
# per-disk S.M.A.R.T. health, CPU/NVMe temperatures, ZFS/mdadm/Ceph detail, and the
# mounted-filesystem breakdown inside LXCs. That gap is what the "Host telemetry not
# installed" banner in the Pulse UI is pointing at.
#
# Thin wrapper around the installer the Pulse SERVER itself serves at
# $PulseUrl/install.sh, so the agent is always version-matched to the server.
#
# ── SECRETS ─────────────────────────────────────────────────────────────────────
# The API token is NEVER passed on the command line: argv is world-readable via
# /proc on a shared node. Supply it one of these ways (checked in order):
#
#   1. $env:PULSE_API_TOKEN                 ← what secrets.env gives you
#   2. -TokenFile <path>                    ← a mode-600 file
#
# ── USAGE ───────────────────────────────────────────────────────────────────────
#   pwsh ./install-pulse-agent.ps1                     # install (or update if present)
#   pwsh ./install-pulse-agent.ps1 -Update             # re-run using saved state
#   pwsh ./install-pulse-agent.ps1 -Uninstall          # remove agent + deregister
#   pwsh ./install-pulse-agent.ps1 -DryRun             # print the command, change nothing
#   pwsh ./install-pulse-agent.ps1 -PulseUrl http://pulse:7655
#   pwsh ./install-pulse-agent.ps1 -Interval 15s
#   pwsh ./install-pulse-agent.ps1 -NoCommands         # observe-only (see PRIVILEGE)
#
# Remote one-liner:
#   pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/install-pulse-agent.ps1' -UseBasicParsing).Content"
#
# ── PRIVILEGE ───────────────────────────────────────────────────────────────────
# The agent runs as ROOT here, deliberately. Upstream offers --least-privilege
# (a dedicated pulse-agent user + scoped sudoers grants), but the installer rejects
# it together with --enable-commands — the low-privilege profile never receives the
# CAP_SETUID/CAP_SETGID ambient grant, so it cannot lxc-attach into guests, which is
# what Docker-in-LXC inventory and Patrol remediation need. We chose commands.
# Pass -NoCommands to drop to observe-only; the least-privilege profile with SMART
# and pct grants is installed instead.
#
# Requirements: PowerShell Core on a PVE node (see install-powershell.sh), root, curl.

param(
    [string]$PulseUrl = $(if ($env:PULSE_URL) { $env:PULSE_URL } else { "http://monitoring.homelab.chrison.internal:7655" }),
    [string]$Interval,
    [string]$TokenFile,
    [switch]$NoCommands,
    [switch]$Update,
    [switch]$Uninstall,
    [switch]$DryRun,
    [switch]$NoPrereqs,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Write-Log  { param($m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Warn { param($m) Write-Host "[warn] $m" -ForegroundColor Yellow }
function Die        { param($m) Write-Host "[error] $m" -ForegroundColor Red; exit 1 }

if ((id -u) -ne 0) { Die "Must run as root (the agent installs a system service)." }

# Refuse to run somewhere that isn't a PVE node unless explicitly forced — the
# --enable-proxmox flag this passes is meaningless elsewhere.
if (-not $Force -and -not (Test-Path /etc/pve) -and -not (Get-Command pveversion -ErrorAction SilentlyContinue)) {
    Die "This does not look like a Proxmox VE node (no /etc/pve, no pveversion). Use -Force to override."
}
if (-not (Get-Command curl -ErrorAction SilentlyContinue)) { Die "curl is required." }

$mode = if ($Uninstall) { "uninstall" } elseif ($Update) { "update" } else { "install" }

# ── Resolve the token into a mode-600 file ──────────────────────────────────────
# Upstream accepts --token-file, so the secret never reaches argv or the process
# table. Anything we create ourselves is removed in the finally block.
$ownTokenFile = $null
function Resolve-Token {
    if ($TokenFile) {
        if (-not (Test-Path $TokenFile)) { Die "Token file not readable: $TokenFile" }
        return $TokenFile
    }
    if (-not $env:PULSE_API_TOKEN) {
        Die "No API token. Set PULSE_API_TOKEN (e.g. 'set -a && . ./secrets.env && set +a'), or pass -TokenFile <path>."
    }
    $f = [System.IO.Path]::GetTempFileName()
    chmod 600 $f | Out-Null
    [System.IO.File]::WriteAllText($f, $env:PULSE_API_TOKEN)
    $script:ownTokenFile = $f
    return $f
}

# Uninstall needs no token — it reads the agent's saved connection state.
$resolvedToken = if ($mode -eq "uninstall") { $null } else { Resolve-Token }

try {
    # ── Prerequisites for the telemetry that justifies the agent ────────────────
    # Without smartmontools there is no S.M.A.R.T.; without lm-sensors, no
    # temperatures. Both are the entire reason for an agent on a hypervisor, so a
    # missing package is a silently degraded install rather than a loud failure.
    if (-not $NoPrereqs -and $mode -ne "uninstall") {
        $missing = @()
        if (-not (Get-Command smartctl -ErrorAction SilentlyContinue)) { $missing += "smartmontools" }
        if (-not (Get-Command sensors  -ErrorAction SilentlyContinue)) { $missing += "lm-sensors" }
        if ($missing.Count -gt 0) {
            Write-Log "Installing prerequisites: $($missing -join ', ')"
            if ($DryRun) {
                Write-Host "    [dry-run] apt-get install -y $($missing -join ' ')"
            } else {
                $env:DEBIAN_FRONTEND = "noninteractive"
                & apt-get update -qq 2>&1 | Out-Null
                & apt-get install -y @missing 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Write-Warn "Could not install $($missing -join ', ') — SMART and/or temperature data will be missing."
                }
            }
        } else {
            Write-Log "Prerequisites present (smartctl, sensors)."
        }
    }

    # ── Build the upstream installer arguments ──────────────────────────────────
    $pulseArgs = @("--url", $PulseUrl)
    switch ($mode) {
        "uninstall" { $pulseArgs += "--uninstall" }
        "update"    { $pulseArgs += @("--update", "--token-file", $resolvedToken) }
        "install"   { $pulseArgs += @("--token-file", $resolvedToken) }
    }
    if ($mode -ne "uninstall") {
        $pulseArgs += "--enable-proxmox"
        if ($NoCommands) {
            # Mutually exclusive with --enable-commands — see the PRIVILEGE note above.
            $pulseArgs += @("--least-privilege", "--grant-smart", "--grant-pct")
        } else {
            # Root profile. Needed for Patrol actions and Docker-in-LXC inventory.
            $pulseArgs += "--enable-commands"
        }
        if ($Interval) { $pulseArgs += @("--interval", $Interval) }
    }

    Write-Log "Pulse server : $PulseUrl"
    Write-Log "Node         : $(hostname)"
    Write-Log "Mode         : $mode"
    # Meaningless for an uninstall, which just tears down whatever profile is there.
    if ($mode -ne "uninstall") {
        Write-Log "Profile      : $(if ($NoCommands) { 'least-privilege (+smart,+pct grants)' } else { 'root (command execution enabled)' })"
    }

    # Mask the token path so -DryRun output and pasted terminal logs stay safe to share.
    $display = ($pulseArgs | ForEach-Object { if ($_ -eq $resolvedToken) { "[token-file]" } else { $_ } }) -join " "

    if ($DryRun) {
        Write-Log "Dry run — would execute:"
        Write-Host "    curl -fsSL $PulseUrl/install.sh | bash -s -- $display"
        exit 0
    }

    Write-Log "Fetching installer from $PulseUrl/install.sh"
    $installer = [System.IO.Path]::GetTempFileName()
    & curl -fsSL --max-time 60 "$PulseUrl/install.sh" -o $installer
    if ($LASTEXITCODE -ne 0) { Die "Could not fetch the installer. Is $PulseUrl reachable from this node?" }
    if ((Get-Item $installer).Length -eq 0) { Die "Installer downloaded empty from $PulseUrl/install.sh" }

    & bash $installer @pulseArgs
    if ($LASTEXITCODE -ne 0) { Die "The Pulse installer failed (see output above)." }
    Remove-Item $installer -Force -ErrorAction SilentlyContinue

    # ── Verify ──────────────────────────────────────────────────────────────────
    if ($mode -eq "uninstall") {
        Write-Log "Agent removed from $(hostname)."
        exit 0
    }

    & systemctl is-active --quiet pulse-agent 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Log "Service pulse-agent is active."
    } else {
        Write-Warn "pulse-agent is not active. Check: systemctl status pulse-agent; journalctl -u pulse-agent -n 50"
    }

    # Confirm the SERVER actually saw the registration — a running local service
    # that never registered is the failure mode worth catching, and it is
    # invisible locally. The token goes in a mode-600 curl config file rather
    # than an -H argument, for the same argv reason as everywhere else.
    $curlCfg = [System.IO.Path]::GetTempFileName()
    chmod 600 $curlCfg | Out-Null
    [System.IO.File]::WriteAllText($curlCfg, "header = `"X-API-Token: $([System.IO.File]::ReadAllText($resolvedToken))`"`n")
    try {
        Write-Log "Waiting for $(hostname) to register with Pulse ..."
        foreach ($i in 1..12) {
            & curl -fsS --config $curlCfg --max-time 10 -o /dev/null `
                "$PulseUrl/api/agents/agent/lookup?hostname=$(hostname)" 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Log "Registered. Host telemetry should now be live in the Pulse UI."
                exit 0
            }
            Start-Sleep -Seconds 5
        }
        Write-Warn "Not registered after 60s. The agent reports on its own interval, so give it a moment; if it stays absent check 'journalctl -u pulse-agent -n 50' on this node."
    } finally {
        Remove-Item $curlCfg -Force -ErrorAction SilentlyContinue
    }
} finally {
    if ($ownTokenFile) { Remove-Item $ownTokenFile -Force -ErrorAction SilentlyContinue }
}
