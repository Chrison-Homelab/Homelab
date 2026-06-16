#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Stop the Windows lock / sign-in screen from interrupting Steam Remote Play.

.DESCRIPTION
  On a single-user gaming VM (Gaming stack VM 1002, gaming-vm-01) the console
  session must stay logged in and unlocked, or Steam Remote Play hits the Windows
  "secure desktop" and shows an "accept secure desktop input from Steam" dialog
  that can only be answered while physically at the PC — a dead end over streaming.

  This script removes every trigger for that lock screen:
    1. no idle auto-lock (machine inactivity limit = off)
    2. lock screen + Win+L disabled
    3. no "require a password on wake"
    4. display never blanks / sleeps / hibernates
    5. screensaver off
    6. (optional, -EnableAutoLogon) passwordless boot straight to the desktop

  It creates the policy keys that don't exist yet, and is safe to re-run.

  NOTE: this does NOT fix the other half — Microsoft RDP forcibly locks the
  console session regardless. Don't RDP into this VM while you want to play;
  use Steam Remote Play as the only remote path.

.PARAMETER EnableAutoLogon
  Also configure auto-login so a reboot returns to the unlocked desktop.
  Prompts for the password. Stores it in PLAINTEXT in the registry — acceptable
  for a throwaway homelab gaming VM; otherwise prefer Sysinternals Autologon
  (LSA-encrypted): https://learn.microsoft.com/sysinternals/downloads/autologon

.EXAMPLE
  .\Disable-SignInScreen.ps1
.EXAMPLE
  .\Disable-SignInScreen.ps1 -EnableAutoLogon
#>

param(
    [switch]$EnableAutoLogon
)

$ErrorActionPreference = 'Stop'

function Set-Reg {
    param($Path, $Name, $Value, [ValidateSet('DWord', 'String')]$Type = 'DWord')
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }   # create the missing key
    New-ItemProperty -Path $Path -Name $Name -PropertyType $Type -Value $Value -Force | Out-Null
    Write-Host ("  [reg] {0}\{1} = {2}" -f $Path, $Name, $Value)
}

Write-Host "`n== 1. No idle auto-lock (machine inactivity limit = off) =="
Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'InactivityTimeoutSecs' 0

Write-Host "== 2. Disable the lock screen + Win+L =="
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization' 'NoLockScreen' 1
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DisableLockWorkstation' 1

Write-Host "== 3. Don't require a password on wake =="
# Hidden power setting "Require a password on wakeup" (GUID), AC + DC, then activate.
$sub = 'SUB_NONE'; $setting = '0e796bdb-100d-47d6-a2d5-f7d2daa51f51'
powercfg /SETACVALUEINDEX SCHEME_CURRENT $sub $setting 0
powercfg /SETDCVALUEINDEX SCHEME_CURRENT $sub $setting 0
powercfg /SETACTIVE SCHEME_CURRENT
Write-Host "  [powercfg] console lock on wake = disabled"

Write-Host "== 4. Never blank/sleep the display =="
powercfg /change monitor-timeout-ac 0
powercfg /change monitor-timeout-dc 0
powercfg /change standby-timeout-ac 0
powercfg /change standby-timeout-dc 0
powercfg /change hibernate-timeout-ac 0
Write-Host "  [powercfg] monitor/standby/hibernate timeouts = never"

Write-Host "== 5. Turn off the screensaver =="
Set-Reg 'HKCU:\Control Panel\Desktop' 'ScreenSaveActive'    '0' String
Set-Reg 'HKCU:\Control Panel\Desktop' 'ScreenSaverIsSecure' '0' String
Set-Reg 'HKCU:\Control Panel\Desktop' 'ScreenSaveTimeOut'   '0' String
rundll32.exe user32.dll, UpdatePerUserSystemParameters   # apply without logoff

if ($EnableAutoLogon) {
    Write-Host "== 6. Auto-login (so a reboot returns to the unlocked desktop) =="
    Write-Warning "AutoAdminLogon stores the password in PLAINTEXT in the registry. OK for a throwaway homelab VM; otherwise prefer Sysinternals Autologon (LSA-encrypted)."
    $user = Read-Host "  Username (e.g. $env:USERNAME)"
    $sec  = Read-Host "  Password" -AsSecureString
    $pw   = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
    $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Set-Reg $wl 'AutoAdminLogon'    '1'   String
    Set-Reg $wl 'DefaultUserName'   $user String
    Set-Reg $wl 'DefaultPassword'   $pw   String
    Set-Reg $wl 'DefaultDomainName' $env:COMPUTERNAME String
}

Write-Host "`nDone. The console session will stay logged in and unlocked." -ForegroundColor Green
Write-Host "Reboot once to confirm it lands straight on the desktop with no sign-in prompt."
