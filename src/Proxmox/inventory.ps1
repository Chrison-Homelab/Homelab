#!/usr/bin/env pwsh

# proxmox-inventory.ps1
# Collects hardware info for Proxmox nodes in a Confluence-friendly format
# PowerShell version for Proxmox nodes

# Set strict error handling
$ErrorActionPreference = "Continue"  # Continue on errors since some commands might fail on different systems

try {
    $hostname = hostname
    
    Write-Host "## Proxmox Host: $hostname"
    Write-Host ""
    
    # CPU Info
    Write-Host "### CPU"
    try {
        $cpuInfo = & lscpu 2>/dev/null
        if ($LASTEXITCODE -eq 0) {
            $cpuInfo | Select-String -Pattern "Model name|Socket|Core|Thread|CPU MHz" | ForEach-Object {
                Write-Host " - $($_.Line)"
            }
        } else {
            # Fallback to /proc/cpuinfo
            $cpuInfo = Get-Content "/proc/cpuinfo" -ErrorAction SilentlyContinue
            $modelName = ($cpuInfo | Select-String "model name" | Select-Object -First 1).Line -replace ".*: ", ""
            $coreCount = ($cpuInfo | Select-String "processor").Count
            if ($modelName) { Write-Host " - Model name: $modelName" }
            if ($coreCount) { Write-Host " - Core(s): $coreCount" }
        }
    } catch {
        Write-Host " - CPU information unavailable"
    }
    
    # Memory Info
    Write-Host ""
    Write-Host "### Memory"
    try {
        $memInfo = & free -h 2>/dev/null
        if ($LASTEXITCODE -eq 0) {
            $memLine = ($memInfo | Select-String "Mem:").Line
            $memFields = $memLine -split '\s+'
            $memTotal = $memFields[1]
            $memUsed = $memFields[2]
            Write-Host " - Total: $memTotal"
            Write-Host " - Used: $memUsed"
        } else {
            # Fallback to /proc/meminfo
            $memInfo = Get-Content "/proc/meminfo" -ErrorAction SilentlyContinue
            $memTotalKB = ($memInfo | Select-String "MemTotal" | ForEach-Object { ($_ -split '\s+')[1] })
            $memFreeKB = ($memInfo | Select-String "MemFree" | ForEach-Object { ($_ -split '\s+')[1] })
            if ($memTotalKB) {
                $memTotalGB = [math]::Round($memTotalKB / 1024 / 1024, 2)
                Write-Host " - Total: ${memTotalGB}GB"
            }
        }
    } catch {
        Write-Host " - Memory information unavailable"
    }
    
    # Storage Info
    Write-Host ""
    Write-Host "### Storage"
    try {
        $storageInfo = & lsblk -o NAME,SIZE,TYPE,MOUNTPOINT 2>/dev/null
        if ($LASTEXITCODE -eq 0) {
            $storageInfo | Select-String -Pattern "disk|part" | ForEach-Object {
                Write-Host " - $($_.Line)"
            }
        } else {
            # Fallback to df
            $dfInfo = & df -h 2>/dev/null
            if ($LASTEXITCODE -eq 0) {
                $dfInfo | Select-Object -Skip 1 | ForEach-Object {
                    Write-Host " - $_"
                }
            }
        }
    } catch {
        Write-Host " - Storage information unavailable"
    }
    
    # Network Interfaces
    Write-Host ""
    Write-Host "### Network Interfaces"
    try {
        $networkInfo = & ip -o link show 2>/dev/null
        if ($LASTEXITCODE -eq 0) {
            $networkInfo | ForEach-Object {
                $interface = ($_ -split ': ')[1] -split '@')[0]
                Write-Host " - $interface"
            }
        } else {
            # Fallback to /sys/class/net
            $interfaces = Get-ChildItem "/sys/class/net" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
            $interfaces | ForEach-Object {
                Write-Host " - $_"
            }
        }
    } catch {
        Write-Host " - Network interface information unavailable"
    }
    
    # PCI Devices (useful for GPU/NIC passthrough)
    Write-Host ""
    Write-Host "### PCI Devices"
    try {
        $pciInfo = & lspci 2>/dev/null
        if ($LASTEXITCODE -eq 0) {
            $pciInfo | Select-String -Pattern "Ethernet|VGA|Storage|USB" | ForEach-Object {
                Write-Host " - $($_.Line)"
            }
        } else {
            Write-Host " - PCI device information unavailable (lspci not found)"
        }
    } catch {
        Write-Host " - PCI device information unavailable"
    }
    
    # System Info
    Write-Host ""
    Write-Host "### System"
    try {
        $manufacturer = & dmidecode -s system-manufacturer 2>/dev/null
        $productName = & dmidecode -s system-product-name 2>/dev/null
        $serialNumber = & dmidecode -s system-serial-number 2>/dev/null
        
        if ($LASTEXITCODE -eq 0 -and $manufacturer) {
            Write-Host " - Manufacturer: $manufacturer"
        } else {
            # Try alternative method
            $dmiInfo = Get-Content "/sys/class/dmi/id/sys_vendor" -ErrorAction SilentlyContinue
            if ($dmiInfo) { Write-Host " - Manufacturer: $dmiInfo" }
        }
        
        if ($LASTEXITCODE -eq 0 -and $productName) {
            Write-Host " - Product Name: $productName"
        } else {
            # Try alternative method
            $dmiInfo = Get-Content "/sys/class/dmi/id/product_name" -ErrorAction SilentlyContinue
            if ($dmiInfo) { Write-Host " - Product Name: $dmiInfo" }
        }
        
        if ($LASTEXITCODE -eq 0 -and $serialNumber) {
            Write-Host " - Serial Number: $serialNumber"
        } else {
            # Try alternative method
            $dmiInfo = Get-Content "/sys/class/dmi/id/product_serial" -ErrorAction SilentlyContinue
            if ($dmiInfo) { Write-Host " - Serial Number: $dmiInfo" }
        }
    } catch {
        Write-Host " - System information unavailable"
    }
    
    # Proxmox-specific information
    Write-Host ""
    Write-Host "### Proxmox Version"
    try {
        $pveVersion = & pveversion 2>/dev/null
        if ($LASTEXITCODE -eq 0) {
            $pveVersion | ForEach-Object {
                Write-Host " - $_"
            }
        } else {
            Write-Host " - Proxmox version information unavailable"
        }
    } catch {
        Write-Host " - Proxmox version information unavailable"
    }
    
} catch {
    Write-Error "Failed to collect inventory information: $($_.Exception.Message)"
    exit 1
}