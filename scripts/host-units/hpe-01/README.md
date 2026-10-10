# hpe-01 host units that read secrets from OpenBao

Each unit starts its job under [`openbao-exec`](../../openbao-exec), which fetches the named keys
from OpenBao with hpe-01's own AppRole and puts them in the job's environment. Nothing is written to
disk. The keys hpe-01 may read are listed in [`openbao-hosts.conf`](../../openbao-hosts.conf).
They replace three hand-made env files (#609, step 5).

| Unit | Keys | Was |
|---|---|---|
| `soulvoice-attend` | `SOULVOICE_COOKIE` | `/etc/soulvoice-attend.env` |
| `seedonly-apply` | `QBIT_PASSWORD SONARR_API_KEY PLEX_TOKEN` | `/root/seedonly/.env`, transient unit |
| `movie-migrate` | `RADARR_API_KEY` | `/root/movie-migrate/.env`, transient unit. **Installed, timer NOT enabled**: the migration finished on 2026-10-02 (volume3 films remaining: 0). Re-run once with `systemctl start movie-migrate.service`. |

Install (after `scripts/openbao-setup-host.sh hpe-01`):

```bash
scp scripts/host-units/hpe-01/*.service scripts/host-units/hpe-01/*.timer root@hpe-01.homelab.chrison.internal:/etc/systemd/system/
ssh root@hpe-01.homelab.chrison.internal 'systemctl daemon-reload && systemctl enable --now soulvoice-attend.timer seedonly-apply.timer'
```

If OpenBao is sealed (after a CT 3007 or hpe-01 restart), the jobs fail with exit 75 and log
`OpenBao is SEALED` until someone unseals it. A missed SoulVoice day resets its attendance streak.

## Home Assistant backup share (no secrets from OpenBao)

Home Assistant (CT 6005) writes its daily 04:30 backups to the NAS share `Homeassistant-Backup`.
hpe-01 mounts the share over CIFS at `/mnt/ha-backup`, and CT 6005 binds that path into Home
Assistant as `/config/backups` (`mp0`). None of this is converge-managed yet, so it lives here so
a rebuilt hpe-01 gets it back (#690, #488).

**1. Credentials file** `/root/.smbcred-ha`, mode `0600`, owned by root:

```
username=homeassistant
password=<the Bitwarden item "Home Assistant" for DSM user homeassistant, nas.homelab.chrison.internal:5001>
```

**2. fstab line.** `uid/gid=100000` is load-bearing: CT 6005 is unprivileged, so its root is 100000
on the host, and without it Home Assistant can't write its own backups.

```
//10.0.0.10/Homeassistant-Backup /mnt/ha-backup cifs credentials=/root/.smbcred-ha,uid=100000,gid=100000,file_mode=0640,dir_mode=0750,vers=3.0,_netdev,nofail 0 0
```

**3. The watchdog** (`ha-backup-mount-watchdog.service` + `.timer`). The mount loses a boot race
against the NAS (`mount error(113)`, EHOSTUNREACH) and `nofail` leaves it failed for good. That
happened at the 7 Sep 2026 reboot and again at the 9 Oct one. The timer remounts it 2 minutes after
boot and checks every 5 minutes after that, which also covers the NAS dropping mid-life. It was
chosen over `x-systemd.automount` because Proxmox would bind the autofs trigger into CT 6005 at
start and might never fire it.

```bash
scp scripts/host-units/hpe-01/ha-backup-mount-watchdog.* root@hpe-01.homelab.chrison.internal:/etc/systemd/system/
ssh root@hpe-01.homelab.chrison.internal 'systemctl daemon-reload && systemctl enable --now ha-backup-mount-watchdog.timer'
```

Test a remount against a genuinely UNMOUNTED share (`umount /mnt/ha-backup`, then start the
service). With the share mounted, `mountpoint -q` short-circuits, and an earlier broken version
passed its test that way.

**4. Known gap:** for up to 2 minutes after a boot, CT 6005 is already running on the empty local
folder. That only matters if a reboot lands on the 04:30 backup. If Home Assistant's backups dir is
ever unwritable it drops its `backup.local` agent and doesn't re-add it: the 04:30 error says
"At least one available backup agent must be selected, got []". Check the mount first, then
re-select the agent in Settings → Backups. HA's `backup_stale_alert` automation flags a failed run.
