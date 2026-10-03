# Synology Scripts

Automation scripts designed to run on a Synology DSM NAS (`DS1813-01`,
`nas.homelab.chrison.internal`).

## Available Scripts

### Monitoring

- **build-static-smartctl.sh**
  - Builds a statically linked smartctl 7.5 from the checksum-verified upstream tarball
  - DSM ships smartctl 6.5, too old for the monitoring agents' S.M.A.R.T. reads; Beszel's
    agent uses this copy (`/usr/local/bin/smartctl-7`) through a PATH shim — keep it installed
  - Runs the build in a throwaway container; nothing is installed on your workstation

## Why this directory has no PowerShell twins

`src/Proxmox/` keeps every script in matched `.sh` / `.ps1` pairs because PowerShell Core can
be installed on the nodes (`install-powershell.sh`). DSM has no such option — it ships a
BusyBox-flavoured userland with no PowerShell package — so scripts here are Bash only.

## Usage

DSM blocks direct root SSH, so scripts run under `sudo` as a user in the `administrators`
group (`homelab`). Two DSM quirks shape how you get a script onto the box:

1. **`scp` fails** when the account has no home directory (`Could not chdir to home
   directory /var/services/homes/homelab`). Pipe the script in over stdin instead.
2. **`sudo` strips the environment.** Use `sudo -E` to keep `PULSE_API_TOKEN`, or stage the
   secret in a mode-600 file and point the script at it with `--token-file`.

Full worked example in [docs/Scripts.md](../../docs/Scripts.md#synology-scripts).

## Secrets

Never pass a token or password as a command-line argument — argv is world-readable through
`/proc`. `install-pulse-agent.sh` refuses `--token` outright and accepts the token only via
`PULSE_API_TOKEN`, `--token-file`, or `--token-stdin`.

Credentials come from the gitignored `secrets.env`, generated from `secrets.env.template` plus
OpenBao:

```bash
scripts/secrets-sync.sh
set -a && . ./secrets.env && set +a     # → PULSE_URL, PULSE_API_TOKEN, SYNOLOGY_PASSWORD
```

## Requirements

- DSM 6.x or 7+ (DSM 7+ gets a systemd unit; DSM 6.x gets an Upstart job)
- An account in the `administrators` group, with SSH enabled
  (Control Panel → Terminal & SNMP → Enable SSH)
- `curl`, and reachability from the NAS to the Pulse server

## Related

- [`src/Proxmox/install-pulse-agent.sh`](../Proxmox/install-pulse-agent.sh) — the same job on
  the hypervisors, where the trade-offs differ (the API already covers most of the fleet, and
  the least-privilege profile is actually available)
- [`stacks/monitoring/`](../../stacks/monitoring/) — the Pulse server itself
