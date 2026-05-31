# UniFi Network Application (test container)

A local [LinuxServer UniFi Network Application](https://docs.linuxserver.io/images/docker-unifi-network-application/)
+ MongoDB — a **safe test target for [UnifiSharp](https://github.com/ChrisonSimtian/UnifiSharp)**.
It runs the same controller software as the live console, so the official
**Integration API (`X-API-KEY`)** behaves the same — **without touching the live
network**. (No real devices adopt here; that's fine for API/read + config testing.)

## Quick start

```bash
cd .containers/unifi
docker compose up -d            # first start pulls images + inits Mongo (~1–2 min)
# open https://localhost:8443   → first-run wizard: create a LOCAL admin account
```

Then create an API key for UnifiSharp:
**Settings → Control Plane → Integrations → Create API Key** (copy it once).

## Point UnifiSharp at it

```bash
export UNIFI_BASE_URL="https://localhost:8443/proxy/network/integration/v1"
export UNIFI_API_KEY="<the key you created>"
export UNIFI_VERIFY_TLS=false    # self-signed cert
# then run UnifiSharp's integration tests / CLI against it
```

## Notes

- **Platform:** Linux/Windows (x86_64). On Apple Silicon the images run under
  emulation — slower, but works for API testing.
- **`8080`/`3478`** are for real device adoption; not needed for API testing.
- DB password (`unifi-dev`) is a throwaway local value (also in `init-mongo.js`).
- Reset everything: `docker compose down -v` (drops the volumes).
- This is for **write/config testing too** once UnifiSharp grows a write path —
  the live network stays untouched.
