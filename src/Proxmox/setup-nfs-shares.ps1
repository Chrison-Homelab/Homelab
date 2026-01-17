#!/usr/bin/env pwsh

# PowerShell version of setup-nfs-shares.sh
# Requires PowerShell Core to be installed on the Proxmox node

param(
    [string]$NasIP = "192.168.179.11",
    [string]$NasName = "DS1813-01"
)

# Set strict error handling
$ErrorActionPreference = "Stop"

$BaseMountPath = "/mnt/$NasName"

# NFS mount options
# If you want to use NFSv4, you must:
# • enable NFSv4 on the NAS
# • set the NFSv4 domain
# • configure /etc/idmapd.conf on Proxmox
$NfsVersion = 3
$NfsMountOptions = "vers=$NfsVersion,rsize=1048576,wsize=1048576,noatime,nodiratime,async"

Write-Host "=== Discovering NFS exports from $NasIP ===" -ForegroundColor Green

try {
    # Discover NFS exports using showmount
    $showmountOutput = & showmount -e $NasIP 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query NFS exports from $NasIP`: $showmountOutput"
    }
    
    # Parse the output - skip the first line (header) and extract the first column (export path)
    $exports = $showmountOutput | Select-Object -Skip 1 | ForEach-Object {
        ($_ -split '\s+')[0]
    } | Where-Object { $_ -and $_.Trim() -ne "" }
    
    if (-not $exports) {
        throw "No NFS exports found on $NasIP"
    }
    
    Write-Host "Found exports:"
    $exports | ForEach-Object { Write-Host "  $_" }
    
    # TODO: Exclude exports on the wrong subnet
    # This requires additional logic to determine the subnet of the Proxmox host
    # and compare it with the export paths if they contain subnet information.
    # For now, we will mount all discovered exports.
    # TODO: Add filtering logic here if needed
    # Example filtering:
    # $filteredExports = $exports | Where-Object { $_ -notlike "*excluded_pattern*" }
    # $exports = $filteredExports
    
    Write-Host "Creating base directory: $BaseMountPath" -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $BaseMountPath | Out-Null
    
    Write-Host "Installing NFS client utilities..." -ForegroundColor Yellow
    & apt update -y
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to update package lists"
    }
    
    & apt install -y nfs-common
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install nfs-common"
    }
    
    foreach ($export in $exports) {
        # Extract the last part of the export path as the local folder name
        # Example: /volume3/Volume-3 → Volume-3
        $localName = Split-Path $export -Leaf
        $localPath = Join-Path $BaseMountPath $localName
        
        Write-Host "Processing export: $export" -ForegroundColor Cyan
        Write-Host "Local mountpoint: $localPath" -ForegroundColor Cyan
        
        New-Item -ItemType Directory -Force -Path $localPath | Out-Null
        
        # Test mount
        Write-Host "Testing mount..." -ForegroundColor Yellow
        $mountCommand = "mount -t nfs -o $NfsMountOptions `"${NasIP}:${export}`" `"$localPath`""
        Invoke-Expression $mountCommand
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to mount $export to $localPath"
        }
        
        Write-Host "Unmounting test mount..." -ForegroundColor Yellow
        & umount $localPath
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to unmount test mount at $localPath"
        }
        
        # Persist mount in /etc/fstab if mount test was successful
        Write-Host "Persisting mount in /etc/fstab..." -ForegroundColor Yellow
        
        # Create fstab entry
        $fstabEntry = "${NasIP}:${export}  ${localPath}  nfs  ${NfsMountOptions}  0  0"
        
        Write-Host "Adding to /etc/fstab if missing..." -ForegroundColor Yellow
        
        # Check if entry already exists in fstab
        $fstabContent = Get-Content -Path "/etc/fstab" -ErrorAction SilentlyContinue
        $entryExists = $fstabContent | Where-Object { $_.Trim() -eq $fstabEntry.Trim() }
        
        if (-not $entryExists) {
            Add-Content -Path "/etc/fstab" -Value $fstabEntry
            Write-Host "Added fstab entry: $fstabEntry" -ForegroundColor Green
        } else {
            Write-Host "Entry already exists in fstab" -ForegroundColor Gray
        }
    }
    
    Write-Host "Mounting all filesystems..." -ForegroundColor Yellow
    & mount -a
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Some filesystems may have failed to mount"
    }
    
    Write-Host "Validating mounts..." -ForegroundColor Yellow
    $dfOutput = & df -h 2>&1
    $naseMounts = $dfOutput | Where-Object { $_ -like "*$NasName*" }
    
    if ($naseMounts) {
        Write-Host "Successfully mounted:" -ForegroundColor Green
        $naseMounts | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
    } else {
        Write-Warning "No mounts detected for $NasName"
    }
    
    Write-Host "=== Dynamic NFS setup complete for $NasName ===" -ForegroundColor Green
    
} catch {
    Write-Error "Setup failed: $($_.Exception.Message)"
    exit 1
}