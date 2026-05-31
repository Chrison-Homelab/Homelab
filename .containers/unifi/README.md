# UniFi OS Server (test container)

A local **UniFi OS Server** — a **safe test target for
[UnifiSharp](https://github.com/ChrisonSimtian/UnifiSharp)**. It runs Ubiquiti's
current self-hosted UniFi OS (via the community
[toquanghieu/unifi-os-server-docker](https://github.com/toquanghieu/unifi-os-server-docker)
image, which downloads the official installer), so its **Integration API behaves
exactly like the live Cloud Gateway** — same `/proxy/network/integration/v1`
path — **without touching the live network**.

> Replaces the old standalone *UniFi Network Application* (now deprecated by
> Ubiquiti — it nags to migrate to UniFi OS Server). This image is **arm64-native**
> (no emulation on Apple Silicon) and **self-contained** (no external MongoDB).

## Quick start

```bash
cd .containers/unifi
docker compose up -d            # first start downloads the UniFi OS installer (~few min)
# open https://localhost:11443  → first-run wizard: create a LOCAL admin account
```

Then create an API key for UnifiSharp:
**Settings → Control Plane → Integrations → Create API Key** (copy it once).

## Point UnifiSharp at it

```bash
export UNIFI_BASE_URL="https://localhost:11443/proxy/network/integration/v1"
export UNIFI_API_KEY="<the key you created>"
export UNIFI_VERIFY_TLS=false    # self-signed cert
unifisharp discover              # or run UnifiSharp's integration tests
```

The base URL matches the **live Cloud Gateway** (`/proxy/network/integration/v1`),
so config carries over between the container and production.

## Notes

- **Requirements:** `cgroup: host` + the capability set in `compose.yml` (UniFi OS
  Server runs systemd + ~10 internal services). Works under OrbStack/Docker Desktop.
- **`8080`/`3478`/`10003`** are for real device adoption/discovery; not needed for
  API testing.
- Reset everything: `docker compose down -v` (drops the data volume).
- For **write/config testing** once UnifiSharp grows a write path — the live
  network stays untouched.
