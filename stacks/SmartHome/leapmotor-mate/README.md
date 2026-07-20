# Leapmotor Mate

Self-hosted, TeslaMate-style companion for a Leapmotor **C10** (works for any
BEV Leapmotor — B05/B10/C10/T03; REEV/range-extender variants are unsupported).
Trip & charge logging, efficiency/SoH trends, cost analysis, and remote control
(climate, locks, charging), with optional MQTT export to Home Assistant.

Upstream: <https://github.com/ProtossBlaster/leapmotor-mate>

> ⚠️ Unofficial. Talks to Leapmotor's reverse-engineered cloud API using mTLS
> client certs. It can break whenever Leapmotor changes their backend — this is
> a hobby integration, treat it as such.

> Part of the **SmartHome** stack — the CT is declared one level up at
> [`../leapmotor-mate.lxc.yaml`](../leapmotor-mate.lxc.yaml); see
> [`../README.md`](../README.md) for the MQTT broker / Home Assistant wiring.

## How it works

```
Leapmotor Cloud  ──mTLS + account login──▶  Mate (poller → SQLite → web UI)  ──▶  optional MQTT → Home Assistant
```

Single container, all state in `./data` (SQLite DB, uploaded certs, secret key).

## Prerequisites

### 1. A DEDICATED Leapmotor account — the important bit

Leapmotor allows **~one active session per account**. If Mate logs in on the
account your phone uses, it will keep kicking your phone out (and vice versa).
So Mate gets its own account and you *share* the car to it.

1. Create a **second Leapmotor account** with a different email from your main one.
2. In the official app, logged in as the account that **owns** the C10, share /
   authorise the vehicle to the new account with **all permissions** and a
   **permanent** duration. Binding is done by **scanning the QR code on the car's
   console**, so do this at the car.
3. **Verify before deploying:** log into the official app as the new account and
   confirm the C10 appears **and** that you can fire a remote command (lock/unlock
   or climate). Shared Leapmotor accounts sometimes get a reduced feature set —
   if remote control is greyed out, the share tier is too limited. Fallback: run
   Mate on your *primary* account and put a fresh secondary account on your phone
   instead (swap which side is dedicated).

### 2. mTLS certificates

Already vendored in [`certs/`](./certs): `app.crt` + `app.key`, downloaded from
[markoceri/leapmotor-certs](https://github.com/markoceri/leapmotor-certs). These
are shared interop certs extracted from the official app (not personal secrets).
You upload them in Mate's first-run wizard.

> **Heads-up on committing `certs/app.key`:** it's a private-key file. It's a
> *public, shared* key (already published upstream), so this isn't a real secret
> leak — but GitHub push protection / secret scanners may still flag it on push.
> If that trips, either allow it (it's a known public key) or `git rm --cached`
> the certs and add `certs/` to `.gitignore` (they're one `curl` away from
> re-downloading — see below).

## Deploy

The CT is defined declaratively as a **member of the SmartHome stack**:
[`../stack.yaml`](../stack.yaml) (stack defaults) and
[`../leapmotor-mate.lxc.yaml`](../leapmotor-mate.lxc.yaml) (a Docker host on CT
**4100**, IoT VLAN 1040, internal-only — adopted in-place, outside SmartHome's
6000-block; see the stack README). It's provisioned via community-scripts like
the rest of the fleet (see `../../Infrastructure/deploy/`), then the compose is
layered on. On the resulting LXC:

```bash
# from a copy of this folder at /opt/leapmotor
cp .env.example .env      # then edit: set MATE_AUTH_PASSWORD if exposing it
docker compose up -d
docker compose logs -f    # watch first boot
```

Open `http://<host>:4000` (or your `MATE_HOST_PORT`) and run the setup wizard:

1. Upload `certs/app.crt` and `certs/app.key`.
2. Enter the **dedicated** account's email, password, and operation PIN.
3. Pick units/currency/timezone; Mate starts polling.

Kick the tyres first without a real account by setting `MATE_DEMO=1` in `.env`.

## Home Assistant (later)

Mate publishes native HA entities via MQTT discovery — enable MQTT in Mate's
settings and point it at your broker. That's the bridge from "standalone app"
to "inside HA" without changing tools.

## Re-downloading the certs

```bash
cd certs
curl -fsSL -o app.crt https://raw.githubusercontent.com/markoceri/leapmotor-certs/main/app.crt
curl -fsSL -o app.key https://raw.githubusercontent.com/markoceri/leapmotor-certs/main/app.key
```

## Files

| Path                          | Purpose                                                   |
|-------------------------------|-----------------------------------------------------------|
| `../leapmotor-mate.lxc.yaml`  | The LXC definition — Docker host on CT 4100 (SmartHome stack root). |
| `compose.yml`                 | The single-service stack.                                 |
| `.env.example`                | Runtime knobs — copy to `.env` (gitignored) and fill in.  |
| `certs/`                      | `app.crt` + `app.key` for mTLS (uploaded in the wizard).  |
| `data/`                       | Created on first run — SQLite, stored creds, secret key.  |
