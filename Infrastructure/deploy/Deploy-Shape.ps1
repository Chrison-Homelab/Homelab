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

    # Optional hard override of the script base URL. When omitted, the URL is
    # derived from spec.source (channel/repo/ref) — see resolution below.
    [string]$BaseUrl
)

$ErrorActionPreference = 'Stop'

# --- deps -----------------------------------------------------------------
if (-not (Get-Module -ListAvailable -Name 'powershell-yaml')) {
    throw "The 'powershell-yaml' module is required. Install with: Install-Module powershell-yaml -Scope CurrentUser"
}
Import-Module powershell-yaml

if (-not (Test-Path $ShapePath)) { throw "Shape not found: $ShapePath" }

# Recursively merge $override over $base (member wins). Nested dictionaries are
# merged key-by-key; scalars/arrays from $override replace $base wholesale.
function Merge-Defaults($base, $override) {
    if ($null -eq $base)     { return $override }
    if ($null -eq $override) { return $base }
    if (($base -isnot [System.Collections.IDictionary]) -or ($override -isnot [System.Collections.IDictionary])) {
        return $override
    }
    $merged = [ordered]@{}
    foreach ($k in $base.Keys)     { $merged[$k] = $base[$k] }
    foreach ($k in $override.Keys) { $merged[$k] = Merge-Defaults $base[$k] $override[$k] }
    return $merged
}

# --- parse + validate -----------------------------------------------------
$shape = (Get-Content -Raw -Path $ShapePath) | ConvertFrom-Yaml

if ($shape.apiVersion -ne 'homelab/v1') { throw "apiVersion must be 'homelab/v1' (got '$($shape.apiVersion)')." }
if ($shape.kind -ne 'LXC') { throw "Deploy-Shape only handles kind: LXC (got '$($shape.kind)'). VM/NASShare are out of scope." }

$spec = $shape.spec
$meta = $shape.metadata
if (-not $meta.name) { throw 'metadata.name is required.' }

# --- inherit stack defaults ------------------------------------------------
# If the member belongs to a stack, merge that stack's spec.defaults underneath
# (member wins). The stack file is the sibling stack.yaml in the same folder.
$stack = $null
if ($meta.stack) {
    $stackPath = Join-Path (Split-Path -Parent (Resolve-Path $ShapePath)) 'stack.yaml'
    if (Test-Path $stackPath) {
        $stack = (Get-Content -Raw -Path $stackPath) | ConvertFrom-Yaml
        if ($stack.kind -ne 'Stack') { throw "$stackPath is not kind: Stack." }
        if ($stack.metadata.name -ne $meta.stack) {
            throw "Member declares stack '$($meta.stack)' but $stackPath is stack '$($stack.metadata.name)'."
        }
        $spec = Merge-Defaults $stack.spec.defaults $spec
        Write-Host "Inherited defaults from stack '$($meta.stack)' ($stackPath)." -ForegroundColor DarkGray
    } else {
        Write-Warning "Member references stack '$($meta.stack)' but no stack.yaml found at $stackPath — proceeding without inherited defaults."
    }
}

# --- required fields (post-merge) ------------------------------------------
foreach ($req in 'app', 'node', 'ctid') {
    if (-not $spec.$req) { throw "spec.$req is required for an LXC deploy (not set on the member or inherited from the stack)." }
}

# --- ctid policy: explicit only on this path -------------------------------
if ("$($spec.ctid)" -eq 'auto') {
    throw "spec.ctid is 'auto'. The community-scripts create path requires an explicit CTID; auto-allocation is an engine (ProxmoxSharp / BL-010) concern. Set a concrete ctid."
}
if ($spec.ctid -notmatch '^\d+$') { throw "spec.ctid must be an integer (got '$($spec.ctid)')." }
# guard rail: explicit ctid must sit inside the owning stack's reserved range
if ($stack -and $stack.spec.ctidRange) {
    $r = $stack.spec.ctidRange
    if ([int]$spec.ctid -lt [int]$r.start -or [int]$spec.ctid -gt [int]$r.end) {
        throw "CTID $($spec.ctid) is outside stack '$($meta.stack)' range $($r.start)-$($r.end)."
    }
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
    if ($spec.network.bridge)  { $vars['var_brg'] = $spec.network.bridge }
    if ($spec.network.vlan)    { $vars['var_vlan'] = $spec.network.vlan }
    if ($spec.network.ipv4)    { $vars['var_net'] = $spec.network.ipv4 }
    if ($spec.network.gateway) { $vars['var_gateway'] = $spec.network.gateway }
    if ($spec.network.ipv6)    { $vars['var_ipv6_method'] = $spec.network.ipv6 }
    if ($spec.network.mtu)     { $vars['var_mtu'] = $spec.network.mtu }
}
if ($spec.nameserver)   { $vars['var_ns'] = $spec.nameserver }
if ($spec.searchdomain) { $vars['var_searchdomain'] = $spec.searchdomain }
if ($spec.features) {
    if ($null -ne $spec.features.nesting) { $vars['var_nesting'] = [int][bool]$spec.features.nesting }
    if ($null -ne $spec.features.fuse)    { $vars['var_fuse']    = [int][bool]$spec.features.fuse }
}
# Tags: stack-default tags (spec.tags, from the merge) + member metadata.tags, deduped.
$allTags = @()
if ($spec.tags) { $allTags += $spec.tags }
if ($meta.tags) { $allTags += $meta.tags }
$allTags = $allTags | Select-Object -Unique
if ($allTags) { $vars['var_tags'] = ($allTags -join ';') }

# Render assignments. Quote any value containing shell-significant chars.
$assignments = foreach ($k in $vars.Keys) {
    $v = [string]$vars[$k]
    if ($v -match "[;\s]") { "$k='$v'" } else { "$k=$v" }
}
$varString  = $assignments -join ' '

# --- resolve script source (channel/repo/ref) ------------------------------
# Precedence: -BaseUrl override > spec.source.repo > spec.source.channel > stable.
if ($BaseUrl) {
    $scriptUrl = "$BaseUrl/ct/$($spec.app).sh"
} else {
    $src     = $spec.source
    $channel = if ($src -and $src.channel) { $src.channel } else { 'stable' }
    $repo    = if ($src -and $src.repo) {
        $src.repo
    } else {
        switch ($channel) {
            'stable' { 'community-scripts/ProxmoxVE' }
            'dev'    { 'community-scripts/ProxmoxVED' }
            default  { throw "Unknown source.channel '$channel' (expected 'stable' or 'dev')." }
        }
    }
    $ref       = if ($src -and $src.ref) { $src.ref } else { 'main' }
    $scriptUrl = "https://raw.githubusercontent.com/$repo/$ref/ct/$($spec.app).sh"
    Write-Host "Source  : $repo@$ref  (channel '$channel')" -ForegroundColor DarkGray
}
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
