# Runbook: volume4 shared `/data` export (prerequisite for #105)

The Media stack (`stacks/Media`) is built on **one shared NFS export on Synology
volume4**, host-mounted on Proxmox and path-bound into every file-touching *arr CT
at `/data`. This export does not exist yet — wire it **before** deploying the stack.

SynoSharp is read-only, so the Synology steps are **manual in DSM**. The Proxmox
steps can be run over SSH. Target node: **hpe-01** (where the stack + Plex live).

Identity used below (pick fixed, unused ids and keep them consistent):

| name | id |
|---|---|
| media uid | `1100` |
| media gid | `1100` |

---

## 1. Synology — create the export (DSM)

1. **Shared folder / dataset** on **volume4**, e.g. mounted as `/volume4/data`.
2. Create the tree (over SSH on the NAS, or via File Station):
   ```bash
   mkdir -p /volume4/data/torrents/{movies,tv} /volume4/data/media/{movies,tv}
   ```
3. **Ownership + inheritance** (so every app's writes stay group-readable/writable):
   ```bash
   groupadd -g 1100 media 2>/dev/null || true
   useradd  -u 1100 -g 1100 -M -s /sbin/nologin media 2>/dev/null || true
   chown -R 1100:1100 /volume4/data
   chmod -R 2775     /volume4/data          # setgid: new files inherit the media group
   # default ACLs (umask-002 equivalent) so future files stay group-rwx:
   synoacltool -add /volume4/data group:media:allow:rwxpdDaARWcCo:fd-- 2>/dev/null || \
     setfacl -R -d -m g:media:rwx /volume4/data
   ```
4. **NFS permissions** on the shared folder (Control Panel → Shared Folder → Edit →
   NFS Permissions → Create):
   - **Hostname/IP:** the Proxmox node(s), e.g. `192.168.179.3` (hpe-01) — or the
     subnet.
   - **Privilege:** Read/Write.
   - **Squash:** **Map all users to admin → No**; instead **Map all users to a
     specified uid/gid** if DSM exposes it, OR set `all_squash,anonuid=1100,anongid=1100`
     in the export options. This neutralises the "CS apps run as root" problem —
     every client write lands as `media:media` regardless of in-CT uid.
   - **Security:** sys; enable "Allow connections from non-privileged ports" and
     "Allow users to access mounted subfolders".

> Why squash + setgid + default ACLs: the *arr CTs write as root (uid 0 → mapped
> high uid in an unprivileged LXC). Without squashing, files land owned by a random
> high uid and the other apps can't read/hardlink them. Squashing to `media:media`
> plus group-inheriting ACLs keeps the whole shared library consistently owned.

---

## 2. Proxmox — add the storage (per node hosting the stack)

```bash
# on hpe-01 (and any other node that will run a Media member):
pvesm add nfs ds1813-nfs-volume-4 \
  --server 192.168.179.11 \
  --export /volume4/data \
  --content images,rootdir \
  --options vers=4

# verify it auto-mounts at the host:
pvesm status | grep ds1813-nfs-volume-4
mountpoint -q /mnt/pve/ds1813-nfs-volume-4 && echo "mounted OK"
ls /mnt/pve/ds1813-nfs-volume-4/data        # should show torrents/ media/
```

> The shape binds the `data` **subpath** (`/mnt/pve/ds1813-nfs-volume-4/data`), so
> the export root can stay clean. `content images,rootdir` lets the same storage
> also back per-app storage volumes later if ever needed.

---

## 3. Attach `/data` + the hookscript (per member)

Until converge applies `spec.mounts` / `spec.hookscript` itself (engine gap — see
`stacks/Media/README.md`), wire each file-touching member (sonarr 5101, radarr 5102,
bazarr 5103, qbittorrent 5104) by hand. The values mirror the shapes exactly.

```bash
# 1. install the pre-start guard on a snippets-enabled storage (once):
install -m 0755 stacks/Media/snippets/ensure-data-mount.sh \
  /var/lib/vz/snippets/ensure-data-mount.sh        # 'local' storage → local:snippets/...

# 2. per member CTID:
for ctid in 5101 5102 5103 5104; do
  pct set "$ctid" -mp0 /mnt/pve/ds1813-nfs-volume-4/data,mp=/data,acl=1,backup=0
  pct set "$ctid" -hookscript local:snippets/ensure-data-mount.sh
done
```

prowlarr (5100), seerr (5105), flaresolverr (5107) get **no** `/data` and **no**
hookscript — they touch no media files.

---

## 4. Validate (the acceptance checks)

```bash
# writes from two different CTs land as the SAME uid/gid (media:media via squash):
pct exec 5104 -- sh -c 'touch /data/torrents/tv/.probe-qbit && ls -n /data/torrents/tv/.probe-qbit'
pct exec 5101 -- sh -c 'touch /data/media/tv/.probe-sonarr && ls -n /data/media/tv/.probe-sonarr'

# hardlink works ACROSS torrents <-> media (same filesystem → instant-move):
pct exec 5101 -- sh -c 'ln /data/torrents/tv/.probe-qbit /data/media/tv/.probe-link && \
  echo "hardlink OK" && rm -f /data/torrents/tv/.probe-qbit /data/media/tv/.probe-* /data/media/tv/.probe-sonarr'

# the guard refuses start when the NAS is down (simulate on a test CT, not live data):
#   umount /mnt/pve/ds1813-nfs-volume-4 ; pct start <ctid>  -> should ABORT pre-start
```

Acceptance (from #105): all mounts host-level + gated (no in-guest mounts), library
readable across every app, hardlinks span `torrents`↔`media`.
