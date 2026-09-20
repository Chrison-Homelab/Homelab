#!/usr/bin/env bash
# Alert on SoulVoice torrents whose swarm is starved of seeds, while it still is.
#
# Upload on a private tracker is earned in the first few hours of a torrent's life and
# then stops: every one of six torrents grabbed on 2026-09-19 went to zero leechers
# within a day, after the first had already earned 4.3 GB. Holding them afterwards costs
# nothing but earns nothing either. So the thing worth being told about is a *fresh*
# swarm with far more leechers than seeds, early enough to still be the second source.
#
# Deliberately NOT restricted to freeleech. During a site-wide free event everything is
# free anyway, and once the event ends a paid download still serves the newcomer exam's
# download requirement, which freeleech cannot progress at all. The alert states the
# download cost so the choice is an informed one.
#
# Scanning the NEWEST torrents does not work, and it is worth saying why. Measured on
# 2026-09-20, the newest-50 window spans only ~10h and its best entries are S17/L7, S54/L6,
# S20/L4 - seeder-swamped, ratio 0.1-0.4. Fresh torrents are grabbed by everyone at once and
# a small one completes for all of them within minutes, so it goes straight to many-seeders.
# The starved state is MID-LIFE for LARGE torrents: Santita was S1/L33 at 16h old precisely
# because 36.9GB takes hours to finish, so leechers accumulate while one seeder exists.
# Hence a keyword sweep across the catalogue rather than a recency window.
set -euo pipefail

PROWLARR=${PROWLARR:-http://10.10.201.184:9696}
ALERTMANAGER=${ALERTMANAGER:-http://10.10.204.35:9093}
PROWLARR_CTID=${PROWLARR_CTID:-5100}
INDEXER_ID=${INDEXER_ID:-17}          # soulvoice-api
STATE_DIR=${STATE_DIR:-/var/lib/soulvoice-hotswarm}
MIN_SEEDERS=${MIN_SEEDERS:-1}         # 0 seeders = nobody holds a complete copy
MIN_LEECHERS=${MIN_LEECHERS:-6}
MIN_RATIO=${MIN_RATIO:-2.5}           # leechers per (seeders+1)
MIN_GB=${MIN_GB:-5}                   # small torrents cannot repay the 72h H&R clock
DRY_RUN=${DRY_RUN:-0}

API_KEY=$(pct exec "$PROWLARR_CTID" -- \
  sed -n 's#.*<ApiKey>\(.*\)</ApiKey>.*#\1#p' /var/lib/prowlarr/config.xml)
[ -n "$API_KEY" ] || { echo "could not read Prowlarr API key from CT $PROWLARR_CTID" >&2; exit 1; }

mkdir -p "$STATE_DIR"
SEEN="$STATE_DIR/seen.json"
[ -f "$SEEN" ] || echo '{}' > "$SEEN"

WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT

# 6 terms x 12 runs/day = 72 queries against the indexer's 100/day limit. Do not add
# terms or shorten the timer without re-checking that ceiling.
echo "[]" > "$WORK/r.json"
for Q in ${TERMS:-2026 2025 2160p 1080p Complete S01}; do
  if curl -sf -m 120 -H "X-Api-Key: $API_KEY" \
      "$PROWLARR/api/v1/search?query=$Q&indexerIds=$INDEXER_ID&type=search&limit=100" \
      -o "$WORK/q.json" 2>/dev/null; then
    python3 - "$WORK" <<'PYX'
import json, sys
w = sys.argv[1]
try:
    new = json.load(open(f"{w}/q.json"))
except Exception:
    new = []
agg = json.load(open(f"{w}/r.json"))
agg.extend(new)
json.dump(agg, open(f"{w}/r.json", "w"))
PYX
  fi
  sleep 3
done

python3 - "$WORK" "$SEEN" "$MIN_LEECHERS" "$MIN_RATIO" "$MIN_GB" "$MIN_SEEDERS" <<'PY'
import json, re, sys, time

work, seen_path, min_l, min_r, min_gb, min_s = sys.argv[1:7]
min_l, min_r, min_gb, min_s = int(min_l), float(min_r), float(min_gb), int(min_s)

rows = list({r.get("guid") or r.get("title"): r for r in json.load(open(f"{work}/r.json"))}.values())
seen = json.load(open(seen_path))
now = time.time()

# Forget anything a week old so the state file cannot grow without bound, and so a
# torrent that goes hot again much later can alert a second time.
seen = {k: v for k, v in seen.items() if now - v < 7 * 86400}

hot = []
for r in rows:
    s, l = r.get("seeders") or 0, r.get("leechers") or 0
    gb = (r.get("size") or 0) / 2**30
    age = r.get("ageHours") or 0
    # Ratio is the signal. MIN_LEECHERS rejects S0/L1 (ratio 1.0, nobody actually
    # waiting); MIN_GB rejects torrents too small to repay a 72h H&R commitment.
    # Age is deliberately NOT filtered - still starved at 100h is still an opening.
    #
    # MIN_SEEDERS rejects zero-seeder torrents. They score well on ratio (no seeds,
    # some leechers) but nobody holds a complete copy, so the download may never
    # finish - and an incomplete torrent never starts its H&R clock, never seeds,
    # and earns nothing while holding a slot.
    if s < min_s or l < min_l or l / (s + 1) < min_r or gb < min_gb:
        continue
    guid = r.get("guid") or r.get("title")
    if guid in seen:
        continue
    hot.append({
        "guid": guid,
        "title": (r.get("title") or "")[:70],
        "gb": round(gb, 2), "seeders": s, "leechers": l,
        "age": round(age, 1),
        "free": "freeleech" in (r.get("indexerFlags") or []),
        "url": r.get("infoUrl"),
        # NEVER derive this from guid: the API definition builds guid as
        # download.php?id=NNN&passkey=XXXX, so a naive split leaks the passkey
        # into an alert label. infoUrl is details.php?id=NNN&hit=1 - safe.
        "id": (re.search(r"[?&]id=(\d+)", r.get("infoUrl") or "") or [None, ""])[1]
              if re.search(r"[?&]id=(\d+)", r.get("infoUrl") or "") else "",
    })

hot.sort(key=lambda h: h["leechers"] / (h["seeders"] + 1), reverse=True)
json.dump(hot, open(f"{work}/hot.json", "w"), ensure_ascii=False)

for h in hot:
    seen[h["guid"]] = now
json.dump(seen, open(seen_path, "w"))

print(f"scanned={len(rows)} new_hot={len(hot)} tracked={len(seen)}")
for h in hot:
    cost = "FREE" if h["free"] else "paid"
    print(f"  S{h['seeders']}/L{h['leechers']} {h['gb']}GB {h['age']}h {cost}  {h['title'][:52]}")
PY

COUNT=$(python3 -c "import json;print(len(json.load(open('$WORK/hot.json'))))")
[ "$COUNT" -eq 0 ] && { echo "nothing new worth alerting"; exit 0; }

python3 - "$WORK" <<'PY'
import json, sys, datetime
w = sys.argv[1]
hot = json.load(open(f"{w}/hot.json"))
best = hot[0]
now = datetime.datetime.now(datetime.timezone.utc)
lines = [f"{h['gb']}GB S{h['seeders']}/L{h['leechers']} {h['age']}h "
         f"{'FREE' if h['free'] else 'paid'}  {h['title']}" for h in hot]
alert = [{
    "labels": {
        "alertname": "SoulVoiceHotSwarm",
        "severity": "info",
        "service": "soulvoice",
        "instance": "pt.soulvoice.club",
        # Vary a label per batch, otherwise Alertmanager treats every run as the same
        # alert and stays quiet until repeat_interval - which would lose the swarm.
        "top": best["id"] or best["title"][:40],
    },
    "annotations": {
        "summary": (f"{len(hot)} seed-starved swarm(s) on SoulVoice - best "
                    f"S{best['seeders']}/L{best['leechers']} {best['gb']}GB"),
        "description": "Upload is earned while the swarm is hot; these drain within a day.\n"
                       + "\n".join(lines),
        "top_url": best["url"] or "",
    },
    "startsAt": now.isoformat(),
    "endsAt": (now + datetime.timedelta(hours=3)).isoformat(),
}]
json.dump(alert, open(f"{w}/alert.json", "w"), ensure_ascii=False)
print(alert[0]["annotations"]["summary"])
PY

if [ "$DRY_RUN" = "1" ]; then
  echo "--- DRY_RUN, would POST: ---"; cat "$WORK/alert.json"; exit 0
fi

curl -sf -m 30 -X POST -H "Content-Type: application/json" \
  --data @"$WORK/alert.json" "$ALERTMANAGER/api/v2/alerts" \
  && echo "raised SoulVoiceHotSwarm on the bus" \
  || { echo "failed to POST to Alertmanager" >&2; exit 1; }
