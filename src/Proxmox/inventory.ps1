#!/usr/bin/env pwsh
# inventory.ps1
#
# PowerShell version of the inventory script for Proxmox nodes
# Collects hardware information in Markdown format with comprehensive error handling
# 
# Usage: 
#   Direct execution: pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/inventory.ps1' -UseBasicParsing).Content"
#   Local execution: pwsh ./inventory.ps1
#
# Output: Markdown-formatted hardware inventory including CPU, memory, storage, network, PCI devices, and Proxmox version
# Requirements: PowerShell Core, lscpu, free, lsblk, ip, lspci, dmidecode, pveversion

# proxmox-inventory.ps1
# Collects hardware info for Proxmox nodes in a Confluence-friendly format
# PowerShell version for Proxmox nodes

# Set strict error handling
$ErrorActionPreference = "Continue"  # Continue on errors since some commands might fail on different systems

try {
    $hostname = hostname
    
    Write-Host "## Proxmox Host: $hostname"
 
    Write-Host ""

    Write-Host "### CPU"
    Get-Cpu-Information
    Write-Host ""

    Write-Host "### Memory"
    Get-Memory-Information

    Write-Host ""

    Write-Host "### Storage"
    Get-Storage-Information

    Write-Host ""

    Write-Host "### Network Interfaces"
    Get-Network-Information

    Write-Host ""

    Write-Host "### PCI Devices"
    Get-Pci-Information

    Write-Host ""

    Write-Host "### System"
    Get-System-Information

    Write-Host ""

    Write-Host "### Proxmox Version"
    Get-Proxmox-Information
    
    function Get-Cpu-Information {
        # CPU Info
        try {
            $cpuInfo = & lscpu 2>/dev/null
            if ($LASTEXITCODE -eq 0) {
                $cpuInfo | Select-String -Pattern "Model name|Socket|Core|Thread|CPU MHz" | ForEach-Object {
                    Write-Host " - $($_.Line)"
                }
            }
            else {
                # Fallback to /proc/cpuinfo
                $cpuInfo = Get-Content "/proc/cpuinfo" -ErrorAction SilentlyContinue
                $modelName = ($cpuInfo | Select-String "model name" | Select-Object -First 1).Line -replace ".*: ", ""
                $coreCount = ($cpuInfo | Select-String "processor").Count
                if ($modelName) { Write-Host " - Model name: $modelName" }
                if ($coreCount) { Write-Host " - Core(s): $coreCount" }
            }
        }
        catch {
            Write-Host " - CPU information unavailable"
        }
    }
    
    function Get-Memory-Information {
        # Memory Info
        try {
            $memInfo = & free -h 2>/dev/null
            if ($LASTEXITCODE -eq 0) {
                $memLine = ($memInfo | Select-String "Mem:").Line
                $memFields = $memLine -split '\s+'
                $memTotal = $memFields[1]
                $memUsed = $memFields[2]
                Write-Host " - Total: $memTotal"
                Write-Host " - Used: $memUsed"
            }
            else {
                # Fallback to /proc/meminfo
                $memInfo = Get-Content "/proc/meminfo" -ErrorAction SilentlyContinue
                $memTotalKB = ($memInfo | Select-String "MemTotal" | ForEach-Object { ($_ -split '\s+')[1] })
                $memFreeKB = ($memInfo | Select-String "MemFree" | ForEach-Object { ($_ -split '\s+')[1] })
                if ($memTotalKB) {
                    $memTotalGB = [math]::Round($memTotalKB / 1024 / 1024, 2)
                    Write-Host " - Total: ${memTotalGB}GB"
                }
            }
        }
        catch {
            Write-Host " - Memory information unavailable"
        }
    }
    
    function Get-Storage-Information {
        # Storage Info
        try {
            $storageInfo = & lsblk -o NAME, SIZE, TYPE, MOUNTPOINT 2>/dev/null
            if ($LASTEXITCODE -eq 0) {
                $storageInfo | Select-String -Pattern "disk|part" | ForEach-Object {
                    Write-Host " - $($_.Line)"
                }
            }
            else {
                # Fallback to df
                $dfInfo = & df -h 2>/dev/null
                if ($LASTEXITCODE -eq 0) {
                    $dfInfo | Select-Object -Skip 1 | ForEach-Object {
                        Write-Host " - $_"
                    }
                }
            }
        }
        catch {
            Write-Host " - Storage information unavailable"
        }
    }
    
    function Get-Network-Information {
        # Network Interfaces
        try {
            $networkInfo = & ip -o link show 2>/dev/null
            if ($LASTEXITCODE -eq 0) {
                $networkInfo | ForEach-Object {
                    $interface = (($_ -split ': ')[1] -split '@')[0]
                    Write-Host " - $interface"
                }
            }
            else {
                # Fallback to /sys/class/net
                $interfaces = Get-ChildItem "/sys/class/net" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
                $interfaces | ForEach-Object {
                    Write-Host " - $_"
                }
            }
        }
        catch {
            Write-Host " - Network interface information unavailable"
        }
    }
    
    function Get-Pci-Information {
        # PCI Devices (useful for GPU/NIC passthrough)
        try {
            $pciInfo = & lspci 2>/dev/null
            if ($LASTEXITCODE -eq 0) {
                $pciInfo | Select-String -Pattern "Ethernet|VGA|Storage|USB" | ForEach-Object {
                    Write-Host " - $($_.Line)"
                }
            }
            else {
                Write-Host " - PCI device information unavailable (lspci not found)"
            }
        }
        catch {
            Write-Host " - PCI device information unavailable"
        }
    }
    
    function Get-System-Information {
        # System Info
        try {
            $manufacturer = & dmidecode -s system-manufacturer 2>/dev/null
            $productName = & dmidecode -s system-product-name 2>/dev/null
            $serialNumber = & dmidecode -s system-serial-number 2>/dev/null
            
            if ($LASTEXITCODE -eq 0 -and $manufacturer) {
                Write-Host " - Manufacturer: $manufacturer"
            }
            else {
                # Try alternative method
                $dmiInfo = Get-Content "/sys/class/dmi/id/sys_vendor" -ErrorAction SilentlyContinue
                if ($dmiInfo) { Write-Host " - Manufacturer: $dmiInfo" }
            }
            
            if ($LASTEXITCODE -eq 0 -and $productName) {
                Write-Host " - Product Name: $productName"
            }
            else {
                # Try alternative method
                $dmiInfo = Get-Content "/sys/class/dmi/id/product_name" -ErrorAction SilentlyContinue
                if ($dmiInfo) { Write-Host " - Product Name: $dmiInfo" }
            }
            
            if ($LASTEXITCODE -eq 0 -and $serialNumber) {
                Write-Host " - Serial Number: $serialNumber"
            }
            else {
                # Try alternative method
                $dmiInfo = Get-Content "/sys/class/dmi/id/product_serial" -ErrorAction SilentlyContinue
                if ($dmiInfo) { Write-Host " - Serial Number: $dmiInfo" }
            }
        }
        catch {
            Write-Host " - System information unavailable"
        }
    }
    
    function Get-Proxmox-Information {
        # Proxmox-specific information
        try {
            $pveVersion = & pveversion 2>/dev/null
            if ($LASTEXITCODE -eq 0) {
                $pveVersion | ForEach-Object {
                    Write-Host " - $_"
                }
            }
            else {
                Write-Host " - Proxmox version information unavailable"
            }
        }
        catch {
            Write-Host " - Proxmox version information unavailable"
        }
    }
    
}
catch {
    Write-Error "Failed to collect inventory information: $($_.Exception.Message)"
    exit 1
}