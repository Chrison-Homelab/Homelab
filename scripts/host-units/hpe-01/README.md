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
