# Runbook: giving a household member access

Three steps. The rest of this page is the traps.

```
1. Invite them to Plex.                     (that is the whole account-creation step)
2. They sign in at https://identity.chrison.dev  → "Continue with Plex"
3. You put them in a group.                 Directory → Users → <them> → Groups
```

After step 2 they exist and can reach **nothing**. That is the design, not a fault — the
Plex source would otherwise enrol anyone with server access straight into every app. Step 3
is the deliberate grant.

## Which group

| Group | Gets |
|---|---|
| `family` | Audiobookshelf, RomM — as a normal user, not admin |
| `guests` | Nothing yet. Intended for media requests only; bound to no application |
| `homelab-admins` | Everything. This is also the break-glass identity — don't hand it out |

Per-stack groups (`media-admins`, `monitoring-admins`, …) exist so access can be granted a
stack at a time. They are for you, not for the household.

## ⚠ There are three front doors and they behave differently

| URL | Route | Send it to family? |
|---|---|---|
| `audiobookshelf.tao-simon.family` | token-based tunnel — **what the household actually uses** | **Yes** |
| `audiobookshelf.chrison.dev` | Media cloudflared tunnel | Yes — works, but not the habitual one |
| `audiobookshelf.arr.chrison.dev` | Pangolin resource | No — authenticates, then shows nothing |

Pangolin grants every resource to its **Admin** role only, and `family` maps to Member.
Its shape says it outright: *"MEMBER IS NOT A DOWNGRADE TO 'LESS ACCESS' — TODAY IT IS NO
ACCESS AT ALL."* The `.arr` name is the admin path.

So a working login can look broken depending on which link someone was sent — the failure is
silent and identical to a permissions bug.

> ⚠ `tao-simon.family` is a **different zone, and our Cloudflare token cannot read its DNS** —
> but the route is declared in the repo: `stacks/Media/cloudflared.lxc.yaml`, `public: true`,
> pointing at CT 5112. It is the household's habitual door (#322) — the reason the retired
> Seerr kept taking requests until July while we believed nobody was using it.
>
> **Every public hostname that reaches Audiobookshelf needs four redirect URIs registered in
> the authentik blueprint.** This has been got wrong twice — #560 added the apex and was
> believed to be the fix; `tao-simon.family` was still missing, so household sign-ins hit
> `Redirect URI Error` while an admin testing on `.arr` or the apex saw everything work.
>
> The hostname list is not hidden, so check it rather than recall it: the tunnel ingress in
> `stacks/Media/cloudflared.lxc.yaml` plus the Pangolin resource in
> `stacks/Core/pangolin.lxc.yaml`. Both were in the repo each time this was missed.

## Removing someone

Remove them from the Plex server. Both admission checks run on **every** login, so they
immediately cannot authenticate.

Their authentik account and its groups **survive** — deliberate (#316), because they may be
family and the group model, not Plex membership, should own that call. But:

> ⚠ The account surviving is not access surviving. Someone who should keep access *after*
> leaving the Plex server needs a second auth method (password or passkey) on their authentik
> account — and that has to be set up **while they can still log in**, not after.

## What is not written down anywhere else

- **Group membership is database state.** `akadmin`'s is declared in the blueprint; nobody
  else's is. A rebuild from scratch loses it. The nightly vzdump of CT 2014 is what covers it.
- **Audiobookshelf's OIDC settings are UI-only** — group claim, auto-register and the mobile
  redirect URIs live only in its SQLite. ABS exposes no env vars for them, so unlike RomM
  there is no provisioner. A rebuild of CT 5112 silently loses all three and the login just
  stops working, with no diff to point at.
- **Access requests would automate step 3** — and are an authentik **Enterprise** feature
  (~$5/user/mo). Evaluated and declined 2026-09-22; the tables ship anyway, so they look
  available in the database. See `authentik_enterprise_license` — 0 rows.

## Adding a third-party mobile app

The app's scheme (e.g. `audiobooth://oauth`) goes in **Audiobookshelf**, not authentik:
Settings → Authentication → *Allowed Mobile Redirect URIs*. Exact string match, no wildcards
per-entry. `*` is accepted **only as the sole entry** and would let any app on the device
receive the token — use explicit entries.

authentik never sees the app's scheme: the app hands it to ABS, ABS sends authentik its own
`https://…/auth/openid/mobile-redirect` URI, and only hands off to the app afterwards.

## References

- [`authentik-break-glass.md`](authentik-break-glass.md) — recovering the IdP itself
- `stacks/Core/authentik/assets/blueprints/00-homelab-identity.yaml` — groups, providers, bindings
- [#485](https://github.com/Chrison-Homelab/Homelab/issues/485) OIDC migration ·
  [#316](https://github.com/Chrison-Homelab/Homelab/issues/316) Plex source ·
  [#468](https://github.com/Chrison-Homelab/Homelab/issues/468) apps delegate to the IdP
