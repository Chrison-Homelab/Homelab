# ShopDemo — open-source eCommerce bake-off

A **throwaway, LAN-only** stack that stands three self-hostable webshop
platforms up side-by-side on one Docker host, so the shop's owner can click
through each **real storefront + admin** and pick a winner. Deliberately **not**
a formal `Homelab.Stacks.*` submodule (per [ADR-0008](../../docs/adr/ADR-0008-stack-extraction-meta-repo.md)) —
a disposable evaluation doesn't earn the 2-PR tax. Once a winner is chosen we
delete this and formalise just the winner.

## What's in the bake-off

| Platform | Stack | Storefront | Admin | Best for |
|---|---|---|---|---|
| **WooCommerce** | WordPress + PHP | `:8081` | `:8081/wp-admin` | The safe, most-popular default. Endless themes/plugins, biggest community, easiest to hire help for. |
| **Bagisto** | Laravel + PHP | `:8082` | `:8082/admin/login` | Modern, clean admin UX out of the box. Newer, smaller ecosystem. |
| **nopCommerce** | ASP.NET / .NET | `:8083` | `:8083/admin` | Feature-rich (tax/shipping/catalog) .NET shop; fits the wider homelab stack. |

All three are free and open-source. None is internet-facing — reach them on the
Docker host's LAN IP only (e.g. `http://10.10.0.x:8081`).

## Bring it up

```bash
cp stack.env.example stack.env
# edit stack.env — set the passwords (openssl rand -base64 24)
docker compose --env-file stack.env up -d
docker compose logs -f woo-init      # watch WooCommerce self-install (~1–2 min)
```

WooCommerce takes a minute or two on first boot: `woo-init` installs WordPress,
activates WooCommerce + the Storefront theme, and imports the bundled sample
products, then exits (that's expected — it's a one-shot job).

## First-login details

| Platform | URL | User | Password |
|---|---|---|---|
| WooCommerce | `http://<host>:8081/wp-admin` | `WOO_ADMIN_USER` (default `admin`) | `WOO_ADMIN_PASSWORD` from `stack.env` |
| Bagisto | `http://<host>:8082/admin/login` | `admin@example.com` | `admin123` (change after login) |
| nopCommerce | `http://<host>:8083` → install wizard | you set it below | you set it below |

### nopCommerce install wizard (one-time)

First hit on `:8083` redirects to `/install`. Fill in:

- **Admin email / password** — pick your own (these become the store admin).
- **Database type:** `PostgreSQL`
- **Connection string values** (use the "individual fields" option, not raw string):
  - Server / host: `nop-db`
  - Database name: `nopcommerce`
  - Username: `postgres`
  - Password: your `NOP_DB_PASSWORD` from `stack.env`
- Leave **"Create database if it doesn't exist"** ticked (it's already created,
  so this is a harmless no-op).

Click install. It seeds the schema + sample data and drops you at the store.

> The `citext` PostgreSQL extension nopCommerce requires is auto-provisioned into
> `template1` by `nop-initdb/01-citext.sh`, so the wizard installs cleanly. (On the
> live CT this was scripted end-to-end; a fresh `up` just needs the wizard once.)

## Tear it down

```bash
docker compose down          # stop, keep volumes (WooCommerce/nopCommerce state survives)
docker compose down -v       # stop AND wipe all demo data (clean slate)
```

## Notes & caveats

- **Data persistence:** WooCommerce and nopCommerce persist to named volumes.
  Bagisto's official image bundles its own MySQL and we run it stateless — a
  `down`/recreate gives it a fresh store. Fine for a demo.
- **Ports** are `8081/8082/8083`. Change the `ports:` lines in `compose.yml` if
  they clash on the host.
- **Not for production.** No TLS, no backups, demo-grade passwords, fixed
  Bagisto creds. Evaluation only.
- **Headless alternatives** (Medusa, Saleor, Vendure) were left out on purpose:
  they need a separately-built storefront, so they don't give a click-around
  demo. Worth a look later if a "modern headless" direction is wanted.

## Picking the winner → formalise

Once a platform wins, the follow-up is to build it as a proper stack (its own
`compose.yml` + `ingress.json` + Cloudflare tunnel for public access + backups),
either in-tree or — if it grows its own lifecycle — extracted to a
`Homelab.Stacks.Shop` submodule per ADR-0008. Everything here is throwaway.
