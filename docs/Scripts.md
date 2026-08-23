# Scripts

## Proxmox Scripts

All scripts are located in the `src/Proxmox/` directory and are designed to run on Proxmox VE nodes.

## PowerShell

### PowerShell Installer

Installs PowerShell Core on the Proxmox node for running PowerShell scripts. Uses snap package manager as Microsoft doesn't officially support Debian 13 yet.

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/install-powershell.sh)`

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/install-powershell.sh)`

### Inventory

Collects hardware information from Proxmox nodes in Markdown format. Output is formatted for easy copying into Confluence or other documentation. Available in both Bash and PowerShell versions.

**Bash version:**

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/inventory.sh)`

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/inventory.sh)`

**PowerShell version:**

**Direct execution:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/inventory.ps1' -UseBasicParsing).Content"
```

**Using wget (download first):**

```bash
wget https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/inventory.ps1 -O /tmp/inventory.ps1 && pwsh /tmp/inventory.ps1
```

### Hardware Information

Quick hardware overview script that collects vendor and model information for key components (CPU, mainboard, RAM, graphics, NICs).

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/hardware-info.sh)`

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/hardware-info.sh)`

### Detailed Hardware Information

Comprehensive hardware information collector that gathers detailed information about CPU, memory, storage, network, GPU, and system components. More detailed than the basic hardware-info.sh script.

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/get-hardware-info.sh)`

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/get-hardware-info.sh)`

### CPU Snapshot

Collects CPU configuration and usage statistics for VMs and LXC containers. Useful for capacity planning, performance analysis, and resource optimization.

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/proxmox-cpu-snapshot.sh)`

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/proxmox-cpu-snapshot.sh)`

### NFS Shares Setup

Dynamically discovers and mounts NFS exports from a NAS to a Proxmox node. Automatically creates mount points and persists them in /etc/fstab. Available in both Bash and PowerShell versions.

After a longer session with CoPilot, it turns out that setting NFS shares up on the proxmox node itself and sharing it out from there into LXC container is way better for performance. Plus it makes it easier to mount shares, no more messing around with NFS.
This also allows us to at some point add a SSD to the node and use this for caching.

**Bash version:**

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/setup-nfs-shares.sh)`

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/setup-nfs-shares.sh)`

**With parameters:**

```bash
curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/setup-nfs-shares.sh | bash -s -- "192.168.1.100" "MyNAS"
```

**PowerShell version:**

**Direct execution:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content"
```

**With custom parameters:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content" -- -NasIP "192.168.1.100" -NasName "MyNAS"
```

### Pulse Agent

Installs the [Pulse](https://github.com/rcourtman/Pulse) unified agent on a Proxmox node
and registers it with the Pulse server on CT 4001
(`monitoring.homelab.chrison.internal:7655`).

Pulse already reads the cluster over the Proxmox API, so this is **not** how guests, storage
or backups get monitored — those work without any agent. The agent exists solely for what the
API cannot return, which is also what the *"Host telemetry not installed"* banner in the UI
refers to:

| Agent-only | Why it matters |
| --- | --- |
| Per-disk S.M.A.R.T. health | Catches a failing disk before it takes a pool with it |
| CPU / NVMe / drive temperatures | Dead fan, dust-choked mini PC, hot cupboard in summer |
| ZFS / mdadm / Ceph detail | Degraded or resilvering state the API only summarises |
| LXC mounted-filesystem breakdown | The API reports the rootfs, not the mounts |
| Docker containers inside LXCs | Needs `pct exec` from the node |

The script is a thin wrapper around the installer the **Pulse server itself** serves at
`/install.sh`, so the agent is always version-matched to the server and there is no vendored
copy in this repo to drift. It installs `smartmontools` and `lm-sensors` first — without them
the two headline metrics are silently absent — then verifies both that the local service is
active *and* that the server actually saw the registration.

**The API token is never passed on the command line.** argv is world-readable through `/proc`,
so the script refuses `--token` outright and takes the token from `PULSE_API_TOKEN`,
`--token-file`, or `--token-stdin`, handing it to the installer as `--token-file`.
`PULSE_API_TOKEN` and `PULSE_URL` both come from `secrets.env` (see `secrets.env.template`).

**Bash version:**

```bash
set -a && . ./secrets.env && set +a
PULSE_API_TOKEN="$PULSE_API_TOKEN" ./src/Proxmox/install-pulse-agent.sh --dry-run
```

Driving it across the cluster from a workstation checkout — the token travels over the SSH
channel's stdin rather than in the remote command line:

```bash
set -a && . ./secrets.env && set +a
for h in hpe-01 nuc-01 desktop-01; do
    scp -q src/Proxmox/install-pulse-agent.sh "$h:/tmp/"
    printf '%s\n' "$PULSE_API_TOKEN" | ssh "$h" 'bash /tmp/install-pulse-agent.sh --token-stdin'
    ssh "$h" 'rm -f /tmp/install-pulse-agent.sh'
done
```

**CURL:** `PULSE_API_TOKEN=... bash <(curl -fsSL https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/install-pulse-agent.sh)`

**WGET:** `PULSE_API_TOKEN=... bash <(wget -qO- https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/install-pulse-agent.sh)`

**PowerShell version:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Chrison-Homelab/Homelab/main/src/Proxmox/install-pulse-agent.ps1' -UseBasicParsing).Content"
```

**Other modes:** `--update` (re-run from saved connection state), `--uninstall` (removes the
service *and* deregisters from the server), `--dry-run`, `--interval 15s`, `--no-commands`.

#### Why the agent runs as root here

Upstream offers `--least-privilege` (a dedicated `pulse-agent` user plus exact-command sudoers
grants for `smartctl` and `pct`), and the installer **rejects it together with
`--enable-commands`** — the low-privilege profile deliberately never receives the
`CAP_SETUID`/`CAP_SETGID` ambient grant, so it cannot `lxc-attach` into a guest. Docker-in-LXC
inventory and Patrol remediation both need exactly that. We chose command execution, so the
agent runs as root on all three hypervisors.

Pass `--no-commands` to invert the trade: the script then installs the least-privilege profile
with `--grant-smart --grant-pct`, keeping SMART and LXC filesystem capacity while dropping
Patrol actions and Docker-in-LXC.

> Docker-in-LXC inventory needs **both** halves. The agent flag alone looks like it works and
> returns an empty inventory — the server gates the feature too, via
> `PULSE_ENABLE_PROXMOX_GUEST_DOCKER_INVENTORY=true`, set in
> [`stacks/monitoring/podman-host/quadlets/pulse.container`](../stacks/monitoring/podman-host/quadlets/pulse.container).

## Synology Scripts

Scripts in `src/Synology/`, designed to run on a Synology DSM NAS. See
[`src/Synology/README.md`](../src/Synology/README.md).

### Pulse Agent

Installs the Pulse unified agent on the NAS. Unlike the Proxmox nodes, the NAS has **no API
integration in Pulse at all** — without an agent it is simply absent from the dashboard, so
this is the only way to get eyes on it. Reports disk S.M.A.R.T. health, volume capacity,
temperatures, CPU/memory, and every container if Container Manager is installed.

DSM blocks direct root SSH, so run it under `sudo` as a user in the `administrators` group.
`scp` also fails on DSM when the account has no home directory, so pipe the script in over
stdin:

```bash
set -a && . ./secrets.env && set +a
NAS=homelab@nas.homelab.chrison.internal
export SSHPASS="$SYNOLOGY_PASSWORD"

# Stage the script and a mode-600 token file, both over stdin (never argv).
cat src/Synology/install-pulse-agent.sh \
  | sshpass -e ssh -o PreferredAuthentications=password -o PubkeyAuthentication=no "$NAS" \
      'umask 077; cat > /tmp/install-pulse-agent.sh'
printf '%s' "$PULSE_API_TOKEN" \
  | sshpass -e ssh -o PreferredAuthentications=password -o PubkeyAuthentication=no "$NAS" \
      'umask 077; cat > /tmp/.pulse-token'

# stdin now carries the sudo password, so the token comes from the staged file.
printf '%s\n' "$SYNOLOGY_PASSWORD" \
  | sshpass -e ssh -o PreferredAuthentications=password -o PubkeyAuthentication=no "$NAS" \
      'sudo -S -p "" bash /tmp/install-pulse-agent.sh --token-file /tmp/.pulse-token'

# Always clean up.
printf '%s\n' "$SYNOLOGY_PASSWORD" \
  | sshpass -e ssh -o PreferredAuthentications=password -o PubkeyAuthentication=no "$NAS" \
      'rm -f /tmp/.pulse-token /tmp/install-pulse-agent.sh'
```

On the NAS itself, `sudo -E ./install-pulse-agent.sh` — the `-E` matters, because plain `sudo`
strips `PULSE_API_TOKEN` from the environment.

**The md0 / md1 exclusion.** DSM keeps its system partition on `/dev/md0` and swap on
`/dev/md1`, both mirrored across every disk. DSM suppresses their non-critical states; Pulse
treats them as ordinary RAID devices and raises **permanent** critical "unhealthy" alerts while
Storage Manager reports everything fine ([upstream #970](https://github.com/rcourtman/Pulse/issues/970),
closed as an enhancement request and never fixed). Two uncloseable criticals train you to
ignore the alert panel, so the script passes `--disk-exclude md0 --disk-exclude md1` by
default. Data volumes are untouched. `--no-disk-exclude-defaults` restores raw upstream
behaviour.

The agent runs as root on DSM and that is not our choice: upstream **refuses**
`--least-privilege` on appliance platforms (Synology, QNAP, TrueNAS, Unraid) rather than
silently falling back, because their service managers and vendor tooling assume it.

### Static smartctl for the NAS

**Disk health does not work on DSM as shipped.** DSM 7.1.1 carries smartctl **6.5** (2021).
The agent collects S.M.A.R.T. by running

```
smartctl -n standby,3 -i -A -H --json=o /dev/sdX
```

and JSON output only arrived in smartmontools **7.0** — 6.5 ignores `--json`, prints its
banner and exits, so the agent parses nothing and every disk reads `health=UNKNOWN,
temperature=0`. Not a device-type problem: the agent already retries each disk with
`-d sat` itself, so forcing sat through a wrapper changes nothing.

`build-static-smartctl.sh` builds smartctl 7.5 from the official upstream tarball inside a
throwaway Alpine container, verifying the publisher's MD5 *before* it compiles anything.
Static on purpose — DSM 7.1 is glibc ~2.20 on kernel 3.10, so a dynamically linked build
from any current distro will not load there. The build is reproducible: repeated runs
produce the same `sha256`.

```bash
./src/Synology/build-static-smartctl.sh          # → ./smartctl-7
./src/Synology/build-static-smartctl.sh --help   # copy-to-NAS commands
```

Copy it over (`scp` fails on DSM without a home directory, and `/tmp` is `noexec`, so it
must be moved into place before it will run):

```bash
NAS=homelab@nas.homelab.chrison.internal
cat smartctl-7 | ssh "$NAS" 'cat > /tmp/smartctl-7'
ssh -t "$NAS" 'sudo sh -c "mv /tmp/smartctl-7 /usr/local/bin/smartctl-7 \
                           && chown root:root /usr/local/bin/smartctl-7 \
                           && chmod 755 /usr/local/bin/smartctl-7"'
```

`install-pulse-agent.sh` then finds it and writes a systemd drop-in setting
`PULSE_SMARTCTL_PATH`. A **drop-in**, not an inline edit of `pulse-agent.service` — the
upstream installer rewrites that unit on every run and would silently discard an edit.
DSM's own `/usr/bin/smartctl` is left exactly as shipped. Override the location with
`--smartctl-path`; a missing binary is a warning, not a failure.

> `/dev/synoboot` (shown as "Diskstation") stays `UNKNOWN` — it is the DSM boot flash, not
> a S.M.A.R.T.-capable disk. Harmless, and it raises no alert.
