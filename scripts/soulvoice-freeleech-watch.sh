#!/usr/bin/env bash
# Detect a SITE-WIDE freeleech event on SoulVoice and raise it on the alert bus.
#
# Why this works: SoulVoice promotes every NEW torrent to free for 7 days, then 50% for
# another 7 (see its rules.php). So a *recent* torrent being free means nothing. A torrent
# older than 14 days being free can only mean the periodic 全站免费 event is running --
# which is the moment worth acting on, because download costs nothing while it lasts.
#
# Producer for the Alertmanager bus (ADR-0011). Alertmanager expires the alert on its own
# via endsAt, so a finished event self-resolves without this script having to notice.
#
# It also raises the OPPOSITE edge. The end of an event is the moment download starts
# counting towards the 下载增量 exam metric again, and that is easy to sleep through --
# a self-expiring alert says nothing when it lapses. So the last verdict is kept on disk
# and an active->inactive transition raises SoulVoiceFreeleechEnded once.
#
# Runs on hpe-01: it needs `pct exec` to read Prowlarr's API key out of CT 5100 without
# the key ever landing in a config file or the process table here.
set -euo pipefail

PROWLARR=${PROWLARR:-http://10.10.201.184:9696}
ALERTMANAGER=${ALERTMANAGER:-http://10.10.204.35:9093}
PROWLARR_CTID=${PROWLARR_CTID:-5100}
INDEXER_ID=${INDEXER_ID:-17}   # soulvoice-api (bearer token); the HTML definition (16) is disabled
# A torrent older than this is past every per-torrent promo, so free => site-wide.
OLD_HOURS=${OLD_HOURS:-336}       # 14 days
MIN_OLD=${MIN_OLD:-5}             # need this many old torrents before judging
MIN_FREE_RATIO=${MIN_FREE_RATIO:-0.8}
# Last verdict, so the active->inactive edge can be spotted. Only ever written when the
# sample was big enough to judge, so a thin search cannot fake an "ended" alert.
STATE_FILE=${STATE_FILE:-/var/lib/soulvoice/freeleech.state}
DRY_RUN=${DRY_RUN:-0}

API_KEY=$(pct exec "$PROWLARR_CTID" -- \
  sed -n 's#.*<ApiKey>\(.*\)</ApiKey>.*#\1#p' /var/lib/prowlarr/config.xml)
[ -n "$API_KEY" ] || { echo "could not read Prowlarr API key from CT $PROWLARR_CTID" >&2; exit 1; }

WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
echo "[]" > "$WORK/agg.json"

# Broad terms; the definition returns nothing for an empty query.
#
# The term list matters more than it looks. Each search comes back newest-first capped at
# 100, so a handful of generic terms returns nothing but fresh uploads -- and fresh uploads
# are free anyway under the 7-day rule, which makes the site-wide test vacuous (old=0).
# These terms are deliberately varied enough to drag older uploads into the sample; with
# them the observed age span is ~0h to ~950h. Do not trim this list without re-checking
# that a dry run still reports a non-zero `old`.
for Q in ${TERMS:-2026 2025 2160p 1080p BluRay UHDTV Complete 4K S01 WEB-DL}; do
  if curl -sf -m 120 -H "X-Api-Key: $API_KEY" \
      "$PROWLARR/api/v1/search?query=$Q&indexerIds=$INDEXER_ID&type=search&limit=100" \
      -o "$WORK/q.json" 2>/dev/null; then
    python3 - "$WORK" <<'PY'
import json, sys
w = sys.argv[1]
try:
    new = json.load(open(f"{w}/q.json"))
except Exception:
    new = []
agg = json.load(open(f"{w}/agg.json"))
agg.extend(new)
json.dump(agg, open(f"{w}/agg.json", "w"))
PY
  fi
  sleep 3
done

python3 - "$WORK" "$OLD_HOURS" "$MIN_OLD" "$MIN_FREE_RATIO" <<'PY'
import json, sys
w, old_h, min_old, min_ratio = sys.argv[1], float(sys.argv[2]), int(sys.argv[3]), float(sys.argv[4])

d = {r["guid"]: r for r in json.load(open(f"{w}/agg.json"))}.values()
d = list(d)
free = lambda r: "freeleech" in (r.get("indexerFlags") or [])
old = [r for r in d if (r.get("ageHours") or 0) >= old_h]
old_free = [r for r in old if free(r)]

# `determinate` is the difference between "no event" and "could not tell". Without it a
# search that happened to return only fresh uploads would read as inactive and fire a
# bogus "freeleech ended" alert.
verdict = {"unique": len(d), "old": len(old), "old_free": len(old_free),
           "active": False, "determinate": len(old) >= min_old}
if verdict["determinate"] and len(old_free) / len(old) >= min_ratio:
    verdict["active"] = True

# Rank the same way a human would: how starved of seeds is it, scaled by how much
# data there is to serve. Small files with one leecher are not worth the H&R clock.
def score(r):
    s, l = r.get("seeders") or 0, r.get("leechers") or 0
    gb = (r.get("size") or 0) / 2**30
    return l / (s + 1) * gb**0.5

cand = [r for r in d if free(r) and (r.get("leechers") or 0) >= 5
        and (r.get("size") or 0) / 2**30 >= 10]
cand.sort(key=score, reverse=True)
verdict["top"] = [{
    "title": r.get("title", "")[:70],
    "gb": round((r.get("size") or 0) / 2**30, 2),
    "seeders": r.get("seeders") or 0,
    "leechers": r.get("leechers") or 0,
    "url": r.get("infoUrl"),
} for r in cand[:6]]
json.dump(verdict, open(f"{w}/verdict.json", "w"), ensure_ascii=False)

print(f"unique={verdict['unique']} old={verdict['old']} old_free={verdict['old_free']} "
      f"active={verdict['active']} determinate={verdict['determinate']} candidates={len(cand)}")
PY

ACTIVE=$(python3 -c "import json;print(json.load(open('$WORK/verdict.json'))['active'])")
DETERMINATE=$(python3 -c "import json;print(json.load(open('$WORK/verdict.json'))['determinate'])")

if [ "$DETERMINATE" != "True" ]; then
  echo "sample too thin to judge (old < MIN_OLD); leaving state untouched"
  exit 0
fi

PREV=unknown
[ -r "$STATE_FILE" ] && PREV=$(cat "$STATE_FILE")
NOW_STATE=inactive; [ "$ACTIVE" = "True" ] && NOW_STATE=active
if [ "$DRY_RUN" != "1" ]; then
  mkdir -p "$(dirname "$STATE_FILE")"
  printf '%s\n' "$NOW_STATE" > "$STATE_FILE"
fi

if [ "$ACTIVE" != "True" ]; then
  # Only the falling edge is worth saying out loud. A site that has simply been at full
  # price for weeks is not news.
  if [ "$PREV" != "active" ]; then
    echo "no site-wide freeleech detected (prev=$PREV); nothing to raise"
    exit 0
  fi
  echo "site-wide freeleech has ENDED; raising SoulVoiceFreeleechEnded"
  python3 - "$WORK" <<'PY'
import json, sys, datetime
w = sys.argv[1]
now = datetime.datetime.now(datetime.timezone.utc)
alert = [{
    "labels": {
        "alertname": "SoulVoiceFreeleechEnded",
        "severity": "info",
        "service": "soulvoice",
        "instance": "pt.soulvoice.club",
    },
    "annotations": {
        "summary": "SoulVoice site-wide freeleech has ended - downloads now count again",
        "description": "Download is charged at full price from now on, which also means it "
                       "counts towards the 下载增量 exam metric. Prefer fewer, larger "
                       "torrents: 魔力 rewards torrent count, but 平均做种时间 is diluted "
                       "by every fresh torrent added at zero hours.",
    },
    "startsAt": now.isoformat(),
    "endsAt": (now + datetime.timedelta(hours=48)).isoformat(),
}]
json.dump(alert, open(f"{w}/alert.json", "w"), ensure_ascii=False)
PY
  if [ "$DRY_RUN" = "1" ]; then
    echo "--- DRY_RUN, would POST: ---"; cat "$WORK/alert.json"; exit 0
  fi
  curl -sf -m 30 -X POST -H "Content-Type: application/json" \
    --data @"$WORK/alert.json" "$ALERTMANAGER/api/v2/alerts" \
    && echo "raised SoulVoiceFreeleechEnded on the bus" \
    || { echo "failed to POST to Alertmanager" >&2; exit 1; }
  exit 0
fi

python3 - "$WORK" <<'PY'
import json, sys, datetime
w = sys.argv[1]
v = json.load(open(f"{w}/verdict.json"))
now = datetime.datetime.now(datetime.timezone.utc)
# Self-resolving: if the next run does not re-raise it, Alertmanager expires it.
ends = now + datetime.timedelta(hours=14)
lines = [f"{t['gb']}GB S{t['seeders']}/L{t['leechers']}  {t['title']}" for t in v["top"]]
alert = [{
    "labels": {
        "alertname": "SoulVoiceFreeleech",
        "severity": "info",
        "service": "soulvoice",
        "instance": "pt.soulvoice.club",
    },
    "annotations": {
        "summary": f"SoulVoice site-wide freeleech active ({v['old_free']}/{v['old']} old torrents free)",
        "description": "Downloads cost nothing while this lasts. Best seed-starved picks:\n"
                       + "\n".join(lines),
        "top_url": (v["top"][0]["url"] if v["top"] else ""),
    },
    "startsAt": now.isoformat(),
    "endsAt": ends.isoformat(),
}]
json.dump(alert, open(f"{w}/alert.json", "w"), ensure_ascii=False)
print(alert[0]["annotations"]["summary"])
PY

if [ "$DRY_RUN" = "1" ]; then
  echo "--- DRY_RUN, would POST: ---"; cat "$WORK/alert.json"; exit 0
fi

curl -sf -m 30 -X POST -H "Content-Type: application/json" \
  --data @"$WORK/alert.json" "$ALERTMANAGER/api/v2/alerts" \
  && echo "raised SoulVoiceFreeleech on the bus" \
  || { echo "failed to POST to Alertmanager" >&2; exit 1; }
