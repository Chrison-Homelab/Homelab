# Gaming VM — guest setup (post-boot, manual)

The Gaming VMs' **machine** is IaC (config, disks, GPU passthrough via the
`*.vm.yaml` shapes + the converge engine). The **guest OS is a throwaway,
one-time manual install** (see [`../README.md`](../README.md) and
[`../../../docs/plans/115-gaming-vm-steamos.md`](../../../docs/plans/115-gaming-vm-steamos.md)) —
so a few in-guest steps live here as scripts rather than in the shape. They are
**not** applied by `homelab-infra converge`; run them once inside the guest after
install (or after a "state of the art" reinstall).

## Steps (Windows — VM 1002 `gaming-vm-01`)

1. **QEMU guest agent** — install from the attached `virtio-win.iso`
   (`virtio-win-guest-tools.exe`). The shape sets `agent: true`, but the in-guest
   service must be present for clean shutdown, IP reporting, and host-driven
   config (e.g. `qm guest exec`). Verify from the host: `qm agent 1002 ping`.

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
   Or fetch + run remotely (replace with the raw URL once on `main`):
   ```powershell
   irm https://raw.githubusercontent.com/Chrison-dev/Homelab/main/stacks/Gaming/guest-setup/Disable-SignInScreen.ps1 | iex
   ```

## Important: don't RDP into this VM while gaming

Microsoft RDP forcibly **locks the console session** that the GPU renders and
Steam Remote Play streams — no in-guest setting prevents this. Use Steam Remote
Play as the only remote path during play. (With `vga: none` + GPU passthrough the
Proxmox web console is blank too — the displays are the physical GPU output and
Steam's stream.)
