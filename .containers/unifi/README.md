# UniFi OS Server (test container)

A local **UniFi OS Server** — a **safe write-testing target for
[UnifiSharp](https://github.com/Chrison-dev/UnifiSharp)**. It runs Ubiquiti's
current self-hosted UniFi OS (via the community
[toquanghieu/unifi-os-server-docker](https://github.com/toquanghieu/unifi-os-server-docker)
image, which downloads the official installer), so its APIs behave like the live
Cloud Gateway — same `/proxy/network/...` paths — **without touching the live
network**. This is the #102 ("write path") prerequisite: a place to exercise
config writes safely.

> Replaces the old standalone *UniFi Network Application* (now deprecated by
> Ubiquiti). This image is **arm64-native** (no emulation on Apple Silicon) and
> **self-contained** (no external MongoDB).

## Quick start — one command

```bash
cd .containers/unifi
./bootstrap.sh              # up (if needed) + first-run setup + verify + write credentials.env
```

`bootstrap.sh` is **idempotent and reproducible** — no manual first-run wizard:

1. `docker compose up -d` (first boot re-downloads the UniFi OS installer, ~a few min).
2. Completes first-run setup via `POST /api/setup` — creates a local admin
   (`labadmin` / `LabAdmin!2026` by default; override with `UNIFI_USERNAME` /
   `UNIFI_PASSWORD`). Skipped automatically if the admin can already log in.
3. Verifies a real **legacy API** call returns `200`.
4. Writes **`credentials.env`** (gitignored) for UnifiSharp's tests.

```bash
./bootstrap.sh --reset      # docker compose down -v first (wipe), then bootstrap from scratch
```

## Auth: which API, which credential

UniFi OS exposes two APIs on the same host, with **different auth**:

| API | Path | Auth | Write coverage |
|-----|------|------|----------------|
| **Legacy** (classic controller) | `/proxy/network/api/s/<site>/rest/...` | **Session** — `POST /api/auth/login` → `TOKEN` cookie + `X-CSRF-Token` | **Full** (port-forwards, firewall, networks/VLANs) |
| Integration (official) | `/proxy/network/integration/v1/...` | `X-API-KEY` | Partial, rolling out through 2026 |

UnifiSharp's **legacy write adapter** uses the **session** path — it works here and
on any UniFi OS gateway, and needs no API key. (This image exposes no scriptable
key-mint endpoint; the live Cloud Gateway's `X-API-KEY` is created in its cloud
console. On the live gateway that key *also* works on the legacy API, so prod can
set `UNIFI_API_KEY` instead — see ADR-0003 / #102.)

## Point UnifiSharp at it

```bash
cd .containers/unifi && ./bootstrap.sh
set -a && . ./credentials.env && set +a    # UNIFI_LEGACY_BASE_URL / UNIFI_USERNAME / UNIFI_PASSWORD / UNIFI_VERIFY_TLS

cd ../../vendor/UnifiSharp
dotnet test                                 # legacy-adapter live tests auto-run when the creds are set
```

## Notes

- **Requirements:** `cgroup: host` + the capability set in `compose.yml` (UniFi OS
  Server runs systemd + ~10 internal services). Works under OrbStack / Docker Desktop.
- **`8080`/`3478`/`10003`** are for real device adoption/discovery; not needed for
  API testing.
- **Reset everything:** `./bootstrap.sh --reset` (or `docker compose down -v`).
- The live network stays untouched — this is a throwaway local controller.
