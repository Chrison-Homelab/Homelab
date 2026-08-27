# Workshop

What we own and where it is. One member today — **InvenTree** on CT 8000 — a parts
inventory with a REST API, so *"what do we need to buy for this build"* is answered
against real stock instead of assembled by hand ([#508](https://github.com/Chrison-Homelab/Homelab/issues/508)).

| | |
|---|---|
| Web / API | <http://inventory.homelab.chrison.internal> |
| Guest | CT 8000 on `hpe-01`, Ubuntu 24.04, 2 cores / 2 GB / 16 GB |
| Admin | `admin`, password in Bitwarden SM as `INVENTREE_ADMIN_PASSWORD` |
| Backups | nightly vzdump 03:30 → `ds1813-nfs-volume-1`, keep 7d/4w/3m |

```bash
./build.sh Preview --stack Workshop   # dry-run
./build.sh Deploy  --stack Workshop   # live apply
```

## Why its own stack

Every other stack *runs* something — Core is the remote-access lifeline, Monitoring
watches the lab, Media serves it, SmartHome automates the house, DevOps builds it.
This is none of those: it is record-keeping **about** the lab's physical parts, and it
serves every other stack's builds. It is in-tree rather than a `Homelab.Stacks.Workshop`
submodule because [ADR-0008](../../docs/adr/ADR-0008-stack-extraction-meta-repo.md)
extracts *domain* stacks and keeps cross-cutting ones in-tree — and there are no blobs,
no vendor material and no separate audience here to pay the 2-PR tax for.

## Using the API

Token auth. Mint one with the admin credentials, then use it as `Authorization: Token …`:

```bash
set -a && . ./secrets.env && set +a
TOKEN=$(curl -s http://inventory.homelab.chrison.internal/api/user/token/ \
          -u "admin:$INVENTREE_ADMIN_PASSWORD" | jq -r .token)

curl -s -H "Authorization: Token $TOKEN" \
     'http://inventory.homelab.chrison.internal/api/part/?search=esp32' | jq
```

The endpoints that matter for the "check a BOM against stock" job:

| | |
|---|---|
| `/api/part/` | the catalogue — one row per kind of part |
| `/api/stock/` | stock items — quantity **and** which location holds them |
| `/api/stock/location/` | the drawers/bins themselves |
| `/api/bom/` | BOM lines: which parts a build consumes, and how many |

## Things worth not rediscovering

- **Ubuntu, not Debian.** `ct/inventree.sh` installs from packager.io and its install
  script hard-requires it (`grep -qE "^ID=(ubuntu)$"` … *"InvenTree requires Ubuntu"*).
  This is the one member that departs from the lab's Debian norm, and it cannot be
  flipped without leaving the packaged install path.
- **`osVersion` must be quoted** — `24.04` unquoted is a YAML *float*. It also used to
  fail validation even when quoted; the validator now parses through YamlDotNet's
  representation model so a quoted scalar stays a string (see `ShapeValidator`).
- **The first `invoke update` can fail and the second succeeds.** The create run died in
  `migrate` with a `TransactionManagementError` behind a `PRAGMA foreign_key_check`;
  re-running the same command completed cleanly. SQLite plus the services the postinstall
  had already started is the likely cause. If a create reports `CREATE FAILED` here, look
  at whether the CT survived (community-scripts keeps it) and re-converge before rebuilding.
- **The superuser is config, not a command.** `invoke superuser` is Django's *interactive*
  `createsuperuser`, so converge cannot use it. The `inventree` provisioner instead sets
  `admin_user` / `admin_email` / `admin_password_file` and lets InvenTree's own startup
  hook create the account — which skips creation when the username already exists, so a
  restart is not a reset.
- **The password file must not end in a newline.** InvenTree reads it with `read_text()`
  and no strip. `printf`, never `echo`.
- **SQLite, so keep the root disk on `local-lvm`.** Moving it to an NFS volume to save
  pool space would put the database on a filesystem with the wrong locking semantics.
