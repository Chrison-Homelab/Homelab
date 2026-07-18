<#
.SYNOPSIS
  BuildLab guest provisioner - silently installs Visual Studio 2019, 2022 and 2026
  (Community IDE + standalone Build Tools) with minimal .NET/MSBuild workloads, to
  test the Fallout .NET build framework across the three VS toolchains.

.DESCRIPTION
  Staged to C:\BuildLab by Windows setup (sources\$OEM$ tree on the custom ISO) and
  launched by autounattend.xml's FirstLogonCommands on first logon. For each VS
  version it downloads the Microsoft bootstrapper and runs it `--quiet --wait
  --norestart --config <vsconfig>`. Idempotent: skips a product that's already
  installed (detected via vswhere / install path). Logs to C:\BuildLab\logs.

  Editions install side-by-side (different major versions coexist - supported by
  Microsoft). Runs UNACTIVATED VS Community (free for this use) - no keys.

  Tuning: edit ide.vsconfig / buildtools.vsconfig (same folder) - the workload set.

.NOTES
  Standalone .NET SDK: VS ships a bundled SDK, but to build modern targets (the repo
  is net10.0) regardless of VS version, this also installs the .NET SDK channel set
  in $DotnetChannel via the official dotnet-install script (non-fatal if it fails).
#>
[CmdletBinding()]
param(
  [string] $Root = 'C:\BuildLab',
  [string] $DotnetChannel = '10.0'   # standalone SDK channel; '' to skip
)

$ErrorActionPreference = 'Stop'
$logDir = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Start-Transcript -Path (Join-Path $logDir 'provision-vs.log') -Append | Out-Null

# year -> bootstrapper (aka.ms/vs/<Channel>/<Label>/<exe>). Channel: 2019=16, 2022=17,
# 2026=18. Label differs by product line: 2019/2022 use 'release'; VS 2026 (ch 18) uses
# 'stable' (aka.ms/vs/18/release dead-ends at a Bing search page, NOT a real bootstrapper
# -- 18/stable resolves to the genuine download.visualstudio.microsoft.com vs_*.exe).
$editions = @(
  @{ Year = 2019; Channel = 16; Label = 'release' },
  @{ Year = 2022; Channel = 17; Label = 'release' },
  @{ Year = 2026; Channel = 18; Label = 'stable'  }
)
# product -> (bootstrapper exe, vsconfig file, vswhere product id)
$products = @(
  @{ Kind = 'Community';  Exe = 'vs_community.exe';  Config = 'ide.vsconfig';        ProductId = 'Microsoft.VisualStudio.Product.Community'  },
  @{ Kind = 'BuildTools'; Exe = 'vs_buildtools.exe'; Config = 'buildtools.vsconfig'; ProductId = 'Microsoft.VisualStudio.Product.BuildTools' }
)

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

function Test-VsInstalled([int]$channel, [string]$productId) {
  if (-not (Test-Path $vswhere)) { return $false }
  # vswhere -version uses a range; match the major (16/17/18) of this channel.
  $hits = & $vswhere -products $productId -version "[$channel.0,$([int]$channel+1).0)" -property installationVersion 2>$null
  return [bool]$hits
}

function Invoke-VsInstall($edition, $product) {
  $tag  = "VS$($edition.Year) $($product.Kind)"
  if (Test-VsInstalled $edition.Channel $product.ProductId) {
    Write-Host "==> $tag already installed - skip."
    return
  }
  $label = if ($edition.Label) { $edition.Label } else { 'release' }
  $url = "https://aka.ms/vs/$($edition.Channel)/$label/$($product.Exe)"
  $exe = Join-Path $env:TEMP "$($edition.Year)_$($product.Exe)"
  $cfg = Join-Path $Root $product.Config
  Write-Host "==> $tag - downloading $url"
  Invoke-WebRequest -Uri $url -OutFile $exe

  # Guard: a wrong aka.ms channel/label doesn't 404 -- it redirects to a Bing search
  # page that gets saved as an .exe, which then fails with the opaque "file or directory
  # is corrupted and unreadable". Reject any download that isn't a real PE ('MZ' header)
  # so a bad channel fails loudly and skips this product instead of the confusing error.
  $sig = [System.IO.File]::ReadAllBytes($exe)[0..1] -join ','
  if ($sig -ne '77,90') {
    Write-Warning "${tag}: $url did not return a PE executable (likely a dead aka.ms channel/label) - skipping."
    return
  }

  $bootstrapArgs = @('--quiet','--wait','--norestart','--nocache','--config', $cfg)
  Write-Host "==> $tag - installing ($($product.Config)) ..."
  $p = Start-Process -FilePath $exe -ArgumentList $bootstrapArgs -Wait -PassThru
  # 0 = success, 3010 = success/reboot-required, 1641 = success/reboot-initiated.
  if ($p.ExitCode -notin 0,3010,1641) {
    Write-Warning "$tag bootstrapper returned $($p.ExitCode) (see VS install logs in %TEMP%\dd_*)."
  } else {
    Write-Host "==> $tag - done (exit $($p.ExitCode))."
  }
}

try {
  foreach ($e in $editions) {
    foreach ($p in $products) {
      try { Invoke-VsInstall $e $p }
      catch { Write-Warning "VS$($e.Year) $($p.Kind) failed: $($_.Exception.Message)" }
    }
  }

  # Standalone .NET SDK (so modern targets build regardless of VS version).
  if ($DotnetChannel) {
    try {
      Write-Host "==> Installing standalone .NET SDK (channel $DotnetChannel) ..."
      $dotnetInstall = Join-Path $env:TEMP 'dotnet-install.ps1'
      Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dotnetInstall
      & $dotnetInstall -Channel $DotnetChannel -InstallDir "$env:ProgramFiles\dotnet"
    } catch { Write-Warning ".NET SDK install failed (non-fatal): $($_.Exception.Message)" }
  }

  # Stop auto-logon now that provisioning is complete (answer file enabled it).
  $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
  Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value '0' -ErrorAction SilentlyContinue
  Remove-ItemProperty -Path $winlogon -Name DefaultPassword -ErrorAction SilentlyContinue

  Write-Host "==> BuildLab provisioning complete. Installed (vswhere):"
  # -products * is REQUIRED: without it vswhere hides Build Tools instances, so a
  # complete 4-product install would under-report as only the Community editions.
  if (Test-Path $vswhere) { & $vswhere -all -prerelease -products * -property displayName }
}
finally {
  Stop-Transcript | Out-Null
}
