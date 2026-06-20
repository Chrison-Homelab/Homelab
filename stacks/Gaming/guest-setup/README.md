# Gaming VM — guest setup (post-boot, manual)

The Gaming VMs' **machine** is IaC (config, disks, GPU passthrough via the
`*.vm.yaml` shapes + the converge engine). The **guest OS is a throwaway,
one-time manual install** (see [`../README.md`](../README.md) and
[`../../../docs/plans/115-gaming-vm-steamos.md`](../../../docs/plans/115-gaming-vm-steamos.md)) —
so a few in-guest steps live here as scripts rather than in the shape. They are
**not** applied by `homelab-infra converge`; run them once inside the guest after
install (or after a "state of the art" reinstall).

## Steps (Windows — VM 1002 `gaming-vm-01`)

1. **QEMU guest agent** — install the minimal agent MSI from the attached
   `virtio-win.iso` (`guest-agent\qemu-ga-x86_64.msi`) — *not* the full
   `virtio-win-guest-tools.exe` bundle, which can reinstall the GPU/network drivers
   and disturb the working passthrough. The shape sets `agent: true`, but the
   in-guest service must be present for clean shutdown, IP reporting, and
   host-driven config (e.g. `qm guest exec`). Verify from the host: `qm agent 1002 ping`.

2. **Disable the sign-in / lock screen** — [`Disable-SignInScreen.ps1`](Disable-SignInScreen.ps1).
   Steam Remote Play streams the **console session**; if Windows locks (idle, or
   because RDP grabbed the session) it drops to the "secure desktop", and Steam's
   *"accept secure desktop input"* dialog can only be answered physically at the
   PC — a dead end over streaming. This script removes every lock trigger.

   Run in an **elevated** PowerShell:
   ```powershell
   # straight paste:
   .\Disable-SignInScreen.ps1
   # also set up passwordless boot (prompts for the password):
   .\Disable-SignInScreen.ps1 -EnableAutoLogon
   ```
   **Or apply it remotely from the Proxmox host** (needs the guest agent — step 1).
   The Homelab repo is **private**, so `irm`-ing a raw GitHub URL 404s — run the
   machine-wide settings straight through the agent instead (this is what actually
   stops the lock interruption):
   ```bash
   qm guest exec 1002 -- reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v InactivityTimeoutSecs /t REG_DWORD /d 0 /f
   qm guest exec 1002 -- reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization"        /v NoLockScreen          /t REG_DWORD /d 1 /f
   qm guest exec 1002 -- powercfg /change monitor-timeout-ac 0
   qm guest exec 1002 -- powercfg /change standby-timeout-ac 0
   ```
   The per-user bits (Win+L disable, screensaver) need the full script run in the
   user's session; run as SYSTEM the script auto-redirects them to the logged-in
   user's hive, but only while that hive is loaded.

## Important: don't RDP into this VM while gaming

Microsoft RDP forcibly **locks the console session** that the GPU renders and
Steam Remote Play streams — no in-guest setting prevents this. Use Steam Remote
Play as the only remote path during play. (With `vga: none` + GPU passthrough the
Proxmox web console is blank too — the displays are the physical GPU output and
Steam's stream.)
