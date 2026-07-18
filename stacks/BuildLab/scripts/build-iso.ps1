#requires -Version 7.0
<#
.SYNOPSIS
  Bake a fully-unattended Windows 11 install ISO for the BuildLab buildvm (VMID 1100)
  and (optionally) upload it to the Proxmox node's `local` ISO storage.

.DESCRIPTION
  Takes a stock Windows 11 ISO and produces `buildlab-win11-unattended.iso` by adding:
    * \autounattend.xml          — the silent-install answer file (this stack)
    * \virtio\                   — virtio-win drivers (so WinPE sees the virtio-scsi disk)
    * sources\$OEM$\$1\BuildLab\ — provision-vs.ps1 + *.vsconfig + virtio guest tools,
                                   which Windows setup copies to C:\BuildLab; the answer
                                   file's FirstLogonCommands then run the VS installer.

  The homelab engine has NO ISO fetch/build logic — this is the documented one-time
  prerequisite before `./build.sh Deploy --stack BuildLab`. Re-run it whenever the
  answer file or provisioner changes.

  Requires `oscdimg.exe` (Windows ADK → "Deployment Tools"). Install:
    winget install Microsoft.WindowsADK   # then the Deployment Tools feature
  Upload requires `scp`/`ssh` on PATH and key/agent auth to the node (root@<node>).

.PARAMETER Win11Iso
  Path to a stock Windows 11 x64 ISO (free download from Microsoft).

.PARAMETER VirtioIso
  Path to virtio-win.iso. If omitted, the stable release is downloaded.

.PARAMETER OutIso
  Output ISO path. Default: .\buildlab-win11-unattended.iso next to this script.

.PARAMETER Node
  Proxmox node to upload to. Default: desktop-01. Pass -SkipUpload to only build.

.PARAMETER SkipUpload
  Build the ISO but do not scp it to the node.

.EXAMPLE
  pwsh stacks/BuildLab/scripts/build-iso.ps1 -Win11Iso D:\isos\Win11_24H2_English_x64.iso
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Win11Iso,
  [string] $VirtioIso,
  [string] $OutIso,
  [string] $Node = 'desktop-01',
  [switch] $SkipUpload
)

$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $PSScriptRoot          # stacks/BuildLab
$unattend = Join-Path $here 'unattend'
if (-not $OutIso) { $OutIso = Join-Path $PSScriptRoot 'buildlab-win11-unattended.iso' }

# Stable virtio-win download (Fedora's official mirror).
$virtioUrl = 'https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso'
$isoLabel  = 'BUILDLAB_W11'

function Find-Oscdimg {
  $cmd = Get-Command oscdimg.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $candidates = Get-ChildItem -Path `
    'C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools' `
    -Recurse -Filter oscdimg.exe -ErrorAction SilentlyContinue
  if ($candidates) { return ($candidates | Select-Object -First 1).FullName }
  throw "oscdimg.exe not found. Install the Windows ADK 'Deployment Tools' feature (winget install Microsoft.WindowsADK)."
}

function Mount-IsoReadOnly([string] $path) {
  $img = Mount-DiskImage -ImagePath (Resolve-Path $path) -PassThru
  ($img | Get-Volume).DriveLetter
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("buildlab-iso-" + [System.IO.Path]::GetRandomFileName())
Write-Host "==> Work dir: $work"
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
  $oscdimg = Find-Oscdimg
  Write-Host "==> oscdimg: $oscdimg"

  # 1. Fetch virtio-win.iso if not supplied.
  if (-not $VirtioIso) {
    $VirtioIso = Join-Path $work 'virtio-win.iso'
    Write-Host "==> Downloading virtio-win.iso ..."
    Invoke-WebRequest -Uri $virtioUrl -OutFile $VirtioIso
  }

  # 2. Copy the full Windows ISO contents to the writable work tree.
  Write-Host "==> Mounting + copying base Windows ISO ..."
  $winDrive = Mount-IsoReadOnly $Win11Iso
  $src = Join-Path $work 'src'
  New-Item -ItemType Directory -Force -Path $src | Out-Null
  Copy-Item -Path "${winDrive}:\*" -Destination $src -Recurse -Force
  Dismount-DiskImage -ImagePath (Resolve-Path $Win11Iso) | Out-Null
  # Strip read-only attrs inherited from the optical media.
  Get-ChildItem -Path $src -Recurse -Force | ForEach-Object { $_.Attributes = 'Normal' }

  # 3. Inject the answer file at the ISO root.
  Copy-Item (Join-Path $unattend 'autounattend.xml') (Join-Path $src 'autounattend.xml') -Force

  # 4. Inject virtio drivers under \virtio (matches the autounattend DriverPaths).
  Write-Host "==> Mounting + copying virtio drivers ..."
  $vDrive = Mount-IsoReadOnly $VirtioIso
  $virtioDst = Join-Path $src 'virtio'
  New-Item -ItemType Directory -Force -Path $virtioDst | Out-Null
  Copy-Item -Path "${vDrive}:\*" -Destination $virtioDst -Recurse -Force

  # 5. Stage the guest payload → copied to C:\BuildLab by setup ($OEM$ tree).
  $oem = Join-Path $src 'sources\$OEM$\$1\BuildLab'
  New-Item -ItemType Directory -Force -Path $oem | Out-Null
  Copy-Item (Join-Path $unattend 'provision-vs.ps1') $oem -Force
  Copy-Item (Join-Path $unattend '*.vsconfig')        $oem -Force
  # virtio guest tools (QEMU guest agent) — installed by FirstLogonCommands.
  $guestTools = Join-Path $virtioDst 'virtio-win-guest-tools.exe'
  if (Test-Path $guestTools) { Copy-Item $guestTools $oem -Force }
  Dismount-DiskImage -ImagePath (Resolve-Path $VirtioIso) | Out-Null

  # 6. Build a UEFI+BIOS bootable ISO (El Torito, both boot images present on Win media).
  Write-Host "==> Building ISO: $OutIso"
  $etfsboot = Join-Path $src 'boot\etfsboot.com'
  $efisys   = Join-Path $src 'efi\microsoft\boot\efisys_noprompt.bin'
  $bootdata = "2#p0,e,b$etfsboot#pEF,e,b$efisys"
  & $oscdimg -m -o -u2 -udfver102 -l$isoLabel "-bootdata:$bootdata" $src $OutIso
  if ($LASTEXITCODE -ne 0) { throw "oscdimg failed ($LASTEXITCODE)" }
  Write-Host "==> Built $OutIso ($([math]::Round((Get-Item $OutIso).Length/1GB,2)) GB)"

  # 7. Upload to the node's `local` ISO storage.
  if (-not $SkipUpload) {
    $dest = "root@${Node}:/var/lib/vz/template/iso/buildlab-win11-unattended.iso"
    Write-Host "==> Uploading to $dest"
    scp $OutIso $dest
    if ($LASTEXITCODE -ne 0) { throw "scp upload failed ($LASTEXITCODE)" }
    Write-Host "==> Uploaded. Now: ./build.sh Deploy --stack BuildLab"
  } else {
    Write-Host "==> SkipUpload set. Copy $OutIso to <node>:/var/lib/vz/template/iso/ manually."
  }
}
finally {
  Write-Host "==> Cleaning work dir"
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
