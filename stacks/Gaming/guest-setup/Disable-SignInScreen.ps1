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

  SAFE TO RUN REMOTELY (as LocalSystem). When invoked via the QEMU guest agent
  (`qm guest exec`, see README.md) the script runs as SYSTEM — where HKCU is
  SYSTEM's own hive, not the gamer's. It detects that and redirects the per-user
  settings to the *logged-in* user's loaded hive (HKEY_USERS\<sid>) instead of
  silently writing them to the wrong account. The machine-wide settings (the ones
  that actually stop the lock) apply either way.

  NOTE: this does NOT fix the other half — Microsoft RDP forcibly locks the
  console session regardless. Don't RDP into this VM while you want to play;
  use Steam Remote Play as the only remote path.

.PARAMETER EnableAutoLogon
  Also configure auto-login so a reboot returns to the unlocked desktop. Stores
  the password in PLAINTEXT in the registry — acceptable for a throwaway homelab
  gaming VM; otherwise prefer Sysinternals Autologon (LSA-encrypted):
  https://learn.microsoft.com/sysinternals/downloads/autologon

.PARAMETER AutoLogonUser
.PARAMETER AutoLogonPassword
  Credentials for -EnableAutoLogon. Required for a non-interactive run (e.g. via
  the guest agent); when running interactively you're prompted if these are omitted.

.EXAMPLE
  .\Disable-SignInScreen.ps1
.EXAMPLE
  .\Disable-SignInScreen.ps1 -EnableAutoLogon
.EXAMPLE
  .\Disable-SignInScreen.ps1 -EnableAutoLogon -AutoLogonUser gamer -AutoLogonPassword 'hunter2'
#>

param(
    [switch]$EnableAutoLogon,
    [string]$AutoLogonUser,
    [string]$AutoLogonPassword
)

$ErrorActionPreference = 'Stop'

$runningAsSystem = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value -eq 'S-1-5-18'
$interactive     = [Environment]::UserInteractive

function Set-Reg {
    param($Path, $Name, $Value, [ValidateSet('DWord', 'String')]$Type = 'DWord', [switch]$Secret)
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }   # create the missing key
    New-ItemProperty -Path $Path -Name $Name -PropertyType $Type -Value $Value -Force | Out-Null
    $shown = if ($Secret) { '***' } else { $Value }                          # never echo a password
    Write-Host ("  [reg] {0}\{1} = {2}" -f $Path, $Name, $shown)
}

# Where should "per-user" (HKCU-equivalent) values go? Interactively, HKCU is the
# user. As SYSTEM (guest agent), HKCU is SYSTEM's hive — wrong — so target the
# console user's loaded hive under HKEY_USERS, or skip with a warning if we can't.
function Get-UserHiveRoot {
    if (-not $runningAsSystem) { return 'HKCU:' }
    $consoleUser = (Get-CimInstance Win32_ComputerSystem).UserName
    if (-not $consoleUser) {
        Write-Warning 'Running as SYSTEM with no interactive user logged in — skipping per-user settings.'
        return $null
    }
    try {
        $sid = (New-Object Security.Principal.NTAccount($consoleUser)).Translate(
            [Security.Principal.SecurityIdentifier]).Value
    } catch {
        Write-Warning "Could not resolve a SID for '$consoleUser' — skipping per-user settings."
        return $null
    }
    if (-not (Test-Path "Registry::HKEY_USERS\$sid")) {
        Write-Warning "User hive for $consoleUser ($sid) is not loaded — skipping per-user settings."
        return $null
    }
    Write-Host "  (running as SYSTEM -> per-user settings target $consoleUser)"
    return "Registry::HKEY_USERS\$sid"
}

$userRoot = Get-UserHiveRoot

Write-Host "`n== 1. No idle auto-lock (machine inactivity limit = off) =="
Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'InactivityTimeoutSecs' 0

Write-Host "== 2. Disable the lock screen + Win+L =="
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization' 'NoLockScreen' 1
if ($userRoot) {
    Set-Reg (Join-Path $userRoot 'Software\Microsoft\Windows\CurrentVersion\Policies\System') 'DisableLockWorkstation' 1
}

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
if ($userRoot) {
    $desktop = Join-Path $userRoot 'Control Panel\Desktop'
    Set-Reg $desktop 'ScreenSaveActive'    '0' String
    Set-Reg $desktop 'ScreenSaverIsSecure' '0' String
    Set-Reg $desktop 'ScreenSaveTimeOut'   '0' String
    if ($interactive) { rundll32.exe user32.dll, UpdatePerUserSystemParameters }  # refresh own session
}

if ($EnableAutoLogon) {
    Write-Host "== 6. Auto-login (so a reboot returns to the unlocked desktop) =="
    Write-Warning "AutoAdminLogon stores the password in PLAINTEXT in the registry. OK for a throwaway homelab VM; otherwise prefer Sysinternals Autologon (LSA-encrypted)."
    if (-not $AutoLogonUser) {
        if ($interactive) { $AutoLogonUser = Read-Host "  Username (e.g. $env:USERNAME)" }
        else { throw "Non-interactive run: pass -AutoLogonUser (and -AutoLogonPassword)." }
    }
    if (-not $AutoLogonPassword) {
        if ($interactive) {
            $sec = Read-Host "  Password" -AsSecureString
            $AutoLogonPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
                [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
        }
        else { throw "Non-interactive run: pass -AutoLogonPassword." }
    }
    $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Set-Reg $wl 'AutoAdminLogon'    '1'                String
    Set-Reg $wl 'DefaultUserName'   $AutoLogonUser     String
    Set-Reg $wl 'DefaultPassword'   $AutoLogonPassword String -Secret
    Set-Reg $wl 'DefaultDomainName' $env:COMPUTERNAME  String
}

Write-Host "`nDone. The console session will stay logged in and unlocked." -ForegroundColor Green
Write-Host "Reboot once to confirm it lands straight on the desktop with no sign-in prompt."
