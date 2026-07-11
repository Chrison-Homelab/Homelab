# scripts/

Workstation-run helper scripts (run from your Mac/PC, not deployed to nodes).

## `healthcheck.{sh,ps1}` — homelab health check

One command to answer *"did everything come back up?"* — after a power outage,
a reboot, or just to spot-check. Probes every device in
[`healthcheck.hosts`](healthcheck.hosts) at the network + service-port level and
prints a green/red table. **No secrets, no .NET toolchain** — just needs `ping`
and `python3` (or `nc`); works from any machine on the LAN.

```bash
./scripts/healthcheck.sh              # probe everything
./scripts/healthcheck.sh --public     # also test https://proxmox.chrison.dev
```
```powershell
./scripts/healthcheck.ps1 -Public
```

Exit code `0` = all critical hosts up, `1` = a critical host is down (so it
scripts/alerts cleanly). A downed **node** flags whether it looks powered-off vs.
hung, and points at [`../src/Proxmox/wake-node.sh`](../src/Proxmox/wake-node.sh)
to wake it. `desktop-01` is marked `optional` in the inventory because the power
orchestrator (#191) may legitimately have it asleep — it warns rather than fails.

**Inventory** lives in [`healthcheck.hosts`](healthcheck.hosts) (shared by both
twins). Keep it in sync with [`../docs/Devices.md`](../docs/Devices.md); update
IPs there as the legacy `192.168.17x` → `10.10.x` migration (BL-002) completes.

Reachability only — it doesn't yet check per-LXC/VM status or NFS mount health on
each node (that needs the nodes up + Proxmox creds). Natural next extension via
`proxmoxsharp` once the hosts are back.
