#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Render an /Infrastructure shape into a community-scripts.org automated-mode
    invocation and (optionally) run it over SSH on the target Proxmox node.

.DESCRIPTION
    Implements the BL-013 "create" mechanism: read a shape.yaml, map it to the
    community-scripts `var_*` surface, and emit the non-interactive invocation
        mode=generated var_ctid=… … bash -c "$(curl -fsSL …/ct/<app>.sh)"
    `mode=generated` bypasses the whiptail menu (build.func: CHOICE="${mode:-…}"),
    so it runs cleanly over SSH with no TTY.

    Dry-run by default — prints the exact command and does NOT mutate the cluster.
    Pass -Apply to execute. Before applying, the target CTID is checked against
    live cluster resources and the run is refused if it already exists
    (community-scripts create is not idempotent).

    Only `kind: LXC` is supported. Update/destroy lifecycle and NFS/host mounts
    are out of scope here — those belong to ProxmoxSharp (BL-010).

.PARAMETER ShapePath
    Path to the shape YAML file.

.PARAMETER Apply
    Execute the invocation over SSH. Omitted = dry-run (print only).

.PARAMETER Node
    Override the SSH target node (defaults to spec.node from the shape).

.PARAMETER SshUser
    SSH user on the Proxmox node. Default: root.

.PARAMETER BaseUrl
    community-scripts raw base URL. Default: the upstream main branch.

.EXAMPLE
    ./Deploy-Shape.ps1 -ShapePath ../examples/servarr.lxc.yaml
    # dry-run: prints the rendered command

.EXAMPLE
    ./Deploy-Shape.ps1 -ShapePath ./mything.lxc.yaml -Apply
    # checks CTID is free, then deploys over SSH
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ShapePath,

    [switch]$Apply,

    [string]$Node,

    [string]$SshUser = 'root',

    [string]$BaseUrl = 'https://github.com/community-scripts/ProxmoxVE/raw/main'
)

$ErrorActionPreference = 'Stop'

# --- deps -----------------------------------------------------------------
if (-not (Get-Module -ListAvailable -Name 'powershell-yaml')) {
    throw "The 'powershell-yaml' module is required. Install with: Install-Module powershell-yaml -Scope CurrentUser"
}
Import-Module powershell-yaml

if (-not (Test-Path $ShapePath)) { throw "Shape not found: $ShapePath" }

# --- parse + validate -----------------------------------------------------
$shape = (Get-Content -Raw -Path $ShapePath) | ConvertFrom-Yaml

if ($shape.apiVersion -ne 'homelab/v1') { throw "apiVersion must be 'homelab/v1' (got '$($shape.apiVersion)')." }
if ($shape.kind -ne 'LXC') { throw "Deploy-Shape only handles kind: LXC (got '$($shape.kind)'). VM/NASShare are out of scope." }

$spec = $shape.spec
$meta = $shape.metadata
if (-not $meta.name) { throw 'metadata.name is required.' }
foreach ($req in 'app', 'node', 'ctid') {
    if (-not $spec.$req) { throw "spec.$req is required for an LXC deploy." }
}

$targetNode = if ($Node) { $Node } else { $spec.node }

if ($spec.mounts) {
    Write-Warning "Shape declares $($spec.mounts.Count) mount(s) — community-scripts does not provision these. Configure NFS/host mounts as a post-create step (ProxmoxSharp / host-level)."
}

# --- shape -> var_* -------------------------------------------------------
# Ordered so the rendered command is stable/diffable. Only non-empty vars emit.
$vars = [ordered]@{}
$vars['var_hostname']         = $meta.name
$vars['var_ctid']             = $spec.ctid
$vars['var_cpu']              = $spec.cores
$vars['var_ram']              = $spec.memory
$vars['var_disk']             = $spec.disk
if ($null -ne $spec.unprivileged) { $vars['var_unprivileged'] = [int][bool]$spec.unprivileged }
if ($spec.os)                 { $vars['var_os'] = $spec.os }
if ($spec.osVersion)          { $vars['var_version'] = $spec.osVersion }
if ($spec.storage)            { $vars['var_container_storage'] = $spec.storage }
if ($spec.templateStorage)    { $vars['var_template_storage'] = $spec.templateStorage }
if ($spec.network) {
    if ($spec.network.vlan)    { $vars['var_vlan'] = $spec.network.vlan }
    if ($spec.network.ipv4)    { $vars['var_net'] = $spec.network.ipv4 }
    if ($spec.network.gateway) { $vars['var_gateway'] = $spec.network.gateway }
    if ($spec.network.ipv6)    { $vars['var_ipv6_method'] = $spec.network.ipv6 }
}
if ($meta.tags) { $vars['var_tags'] = ($meta.tags -join ';') }

# Render assignments. Quote any value containing shell-significant chars.
$assignments = foreach ($k in $vars.Keys) {
    $v = [string]$vars[$k]
    if ($v -match "[;\s]") { "$k='$v'" } else { "$k=$v" }
}
$varString  = $assignments -join ' '
$scriptUrl  = "$BaseUrl/ct/$($spec.app).sh"
# TERM=xterm: the community-scripts call `clear`, which fails ("TERM not set")
# over an SSH session with no TTY. Setting it keeps the run fully non-interactive.
$remoteCmd  = "TERM=xterm mode=generated $varString bash -c `"`$(curl -fsSL $scriptUrl)`""

# --- output ---------------------------------------------------------------
Write-Host ''
Write-Host "Shape   : $ShapePath" -ForegroundColor Cyan
Write-Host "Target  : $SshUser@$targetNode  (CTID $($spec.ctid), app '$($spec.app)')" -ForegroundColor Cyan
Write-Host "Command :" -ForegroundColor Cyan
Write-Host "  $remoteCmd"
Write-Host ''

if (-not $Apply) {
    Write-Host "DRY-RUN — nothing executed. Re-run with -Apply to deploy." -ForegroundColor Yellow
    return
}

# --- existence guard (create is not idempotent) ---------------------------
Write-Host "Checking CTID $($spec.ctid) is free on the cluster..." -ForegroundColor Cyan
$existing = ssh "$SshUser@$targetNode" "pvesh get /cluster/resources --type vm --output-format json"
if ($LASTEXITCODE -ne 0) { throw "Could not query cluster resources on $targetNode (ssh/pvesh failed)." }
$ids = ($existing | ConvertFrom-Json).vmid
if ($ids -contains [int]$spec.ctid) {
    throw "CTID $($spec.ctid) already exists on the cluster. Refusing to create (community-scripts create is not idempotent). Use ProxmoxSharp lifecycle to update/destroy."
}

# --- apply ----------------------------------------------------------------
Write-Host "Applying over SSH to $targetNode ..." -ForegroundColor Green
ssh "$SshUser@$targetNode" $remoteCmd
if ($LASTEXITCODE -ne 0) { throw "Remote deploy exited with code $LASTEXITCODE." }
Write-Host "Done. CTID $($spec.ctid) deployment invoked on $targetNode." -ForegroundColor Green
