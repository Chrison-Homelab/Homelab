# Runbook: authentik break-glass (recovery key)

How to get back into **authentik** (CT 2014, `identity.chrison.dev`) when normal login is
unavailable — a broken authentication flow, a lost admin credential, a policy binding that
locked out the last account that could edit it, or a custom-CSS change that made the login
page unusable.

This is the recovery path [#469](https://github.com/Chrison-Homelab/Homelab/issues/469)
requires be **proven before anything depends on the IdP**. Four applications now do
(Pangolin, Pulse, Grafana, Forgejo — [#485](https://github.com/Chrison-Homelab/Homelab/issues/485)),
so it is no longer optional.

---

## What this is, and what it is *not*

`ak create_recovery_key` mints a `Token` with intent `recovery` and prints a link at
`/recovery/use-token/<key>/`. Opening that link runs `UseTokenView`, which does exactly
this and nothing more:

```python
login(request, token.user, backend=BACKEND_INBUILT)   # direct Django login
token.delete()                                        # single use
request.session[SESSION_KEY_BRAND_SAFE_MODE] = True   # suppress custom CSS etc.
return redirect("authentik_core:if-user")
```

Three consequences worth internalising, because each one contradicts a reasonable
assumption:

- **No flow is executed.** It does *not* need a recovery flow. `default-recovery-flow` does
  not exist on this instance and the brand carries no `flow_recovery` — that is the
  self-service "Forgot password?" feature, which is unrelated to this path and
  [deliberately not configured](#why-there-is-no-recovery-flow).
- **No email is sent.** No SMTP is configured for authentik anywhere in this repo, and none
  is needed here.
- **Brand safe mode** is set on the session, which suppresses "lock-out-prone customization
  such as custom CSS". So this link still works when a branding change is *what broke the
  login page*.

The token is deleted on redemption. It is single-use by construction — a second click 404s.

---

## Preconditions — and why the chain is not circular

The whole point of a break-glass path is that it shares no dependency with the thing it
recovers. Today it does not:

| Step | Authenticated by | Depends on authentik? |
|---|---|---|
| SSH / console to `hpe-01` | SSH key, or Proxmox console | No |
| Proxmox web UI (if used) | `root@pam` — **local realm** | No |
| `pct enter 2014` | root on the node | No |
| `podman exec` | local `podman` user | No |

> ⚠ **This property is load-bearing and is scheduled to be attacked.**
> [#485](https://github.com/Chrison-Homelab/Homelab/issues/485) plans an OIDC realm for
> Proxmox VE and PDM. The rule recorded there — **add the OIDC realm, never remove the local
> one, and never make OIDC the only realm** — is what keeps this table true. Re-read this
> runbook if that work lands.

---

## 1. Mint a recovery key

On the node, enter the container:

```bash
ssh root@hpe-01.homelab.chrison.internal
pct enter 2014
```

Then — **as the `podman` user, not root**:

```bash
cd /
UID_N=$(id -u podman)
runuser -u podman -- env XDG_RUNTIME_DIR=/run/user/$UID_N \
  DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$UID_N/bus \
  podman exec authentik-server ak create_recovery_key 10 akadmin
```

`10` is **minutes** (the argument is a duration in minutes, default 60 — not years, which is
the common misreading). `akadmin` is the target username.

> ⚠ **`podman exec` as root fails with `no container with name or ID "authentik-server"
> found`.** The stack is rootless podman under the `podman` user (ADR-0009), so root's podman
> has no containers at all — the error names the container, which reads like the container is
> missing rather than like you are the wrong user.
>
> Both env vars are required (podman reaches the user bus via `DBUS_SESSION_BUS_ADDRESS`;
> `systemctl --user` finds the socket via `XDG_RUNTIME_DIR`), and **`cd /` is not
> decoration** — `runuser` keeps the caller's cwd, `pct enter` lands you in `/root`, and the
> rootless user cannot read it. This is the same incantation converge itself uses; see
> `PodmanProvisioner.UserCmd` and [#284](https://github.com/Chrison-Homelab/Homelab/issues/284).
>
> `ak` is spelled out because the image entrypoint (`dumb-init -- ak`) does not apply to
> `podman exec`.

Expected output — a warning, the validity, and a **host-relative** URL:

```
Store this link safely, as it will allow anyone to access authentik as akadmin.
This recovery token is valid for 10 minutes.
/recovery/use-token/<key>/
```

## 2. Redeem it

Join the path to the public origin and open it in a **private window** (so it does not
collide with an existing session):

```
https://identity.chrison.dev/recovery/use-token/<key>/
```

You should land in the user interface, logged in as `akadmin`, with a warning banner reading
*"Used recovery-link to authenticate."*

## 3. Fix the thing, then close the hole

The recovery session is a full login as that user. Do the repair, then:

- Confirm the token is gone — re-opening the same link must 404.
- If the lockout was caused by a **blueprint** change, fix it in
  `stacks/Core/authentik/assets/blueprints/` and converge. Repairing it in the UI leaves the
  estate and git disagreeing, and the next converge reverts your fix.

---

## Verification checklist

Run this **as a drill**, not only in an emergency. Tick all five:

- [ ] The `runuser` command prints a link (not a podman error)
- [ ] The link logs you in as `akadmin` in a private window
- [ ] The "Used recovery-link to authenticate." banner appears
- [ ] Re-opening the same link returns 404 (single-use confirmed)
- [ ] An expired key is refused — mint with duration `1`, wait, confirm 404

### Test log

| Date | By | Result | Notes |
|---|---|---|---|
| 2026-09-05 | Chris | ❌ failed — wrong user | Ran `podman exec` as root from `/root`; `no such container`. Not an authentik fault. Retry with the `runuser` form above. |
| | | | |

---

## If the container will not start at all

The recovery key needs a running `authentik-server`. If CT 2014 itself is broken, the path is
a restore, not a key:

1. Restore CT 2014 from the nightly `vzdump` on `ds1813-nfs-volume-1`
   (`mode: snapshot`, weekday nightly, kept 5d/4w/1m).
2. **Postgres is inside the CT** — container `authentik-postgresql` (`postgres:16`), host bind
   mount `/home/podman/authentik/database` on the CT rootfs `local-lvm:vm-2014-disk-0`. There
   is no separate database backup; restoring the CT restores the database.
3. A vzdump snapshot of a live Postgres is **crash-consistent**, not a clean dump. PG16
   replays WAL on start, so it comes up — but expect a recovery log line, and verify the
   blueprint state afterwards (see below).

> ⚠ **`AUTHENTIK_SECRET_KEY` is not recoverable from the backup alone in any useful sense.**
> It signs sessions *and encrypts values in the database*, so a restored database is
> unreadable without the same key. It lives in **Bitwarden Secrets Manager** and reaches the
> CT as a podman secret via `secrets.env` — that is the authoritative copy. Losing it is
> worse than losing the database.
>
> Note the converse: the vzdump **contains** the podman secrets, so the NFS share holds a copy
> of the key next to the data it decrypts.

After any restore, confirm the blueprint is still applied rather than assuming it:

```bash
podman exec authentik-postgresql psql -U authentik -d authentik -t -A -c \
  "select name, status, last_applied from authentik_blueprints_blueprintinstance
   where path like 'custom/%' and status <> 'successful'"
```

**Empty output means good** — the query asks what is *wrong*. This is the same check
`authentik.lxc.yaml`'s `verify` block runs.

---

## Why there is no recovery flow

`default-recovery-flow` is absent, and the brand has no `flow_recovery`, **on purpose**:

- It is not needed for break-glass — see the top of this runbook.
- It requires SMTP, which authentik does not have here.
- It would put a permanent "Forgot password?" surface on an IdP that is deliberately
  internet-facing and deliberately un-gated (no Pangolin SSO, no CF Access — see the header
  of `stacks/Core/authentik.lxc.yaml`). Upstream's own example flow carries
  `recovery_max_attempts: 5` precisely because that surface is abuse-prone.
- A minted-on-demand, single-use, minutes-long key is the stronger property for a
  single-operator lab.

Revisit when Family accounts are real **and** SMTP exists — at which point it is a
convenience feature, not a recovery mechanism.

---

## References

- `stacks/Core/authentik.lxc.yaml` — the shape, secrets, and the blueprint `verify` block
- `stacks/Core/authentik/quadlets/` — `authentik-server`, `authentik-worker`, `authentik-postgresql`
- `Infrastructure/engine/Converge/PodmanProvisioner.cs` — `UserCmd`, the rootless incantation
- Upstream: <https://docs.goauthentik.io/troubleshooting/login/>
- [#469](https://github.com/Chrison-Homelab/Homelab/issues/469) stand up authentik ·
  [#485](https://github.com/Chrison-Homelab/Homelab/issues/485) OIDC migration ·
  [#513](https://github.com/Chrison-Homelab/Homelab/issues/513) backups
