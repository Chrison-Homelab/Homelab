#!/usr/bin/env bash
# Claim the daily SoulVoice attendance bonus (签到).
#
# NexusPHP gates attendance behind `captcha.attendance.enabled`. SoulVoice has it OFF,
# and attendance.php then claims on a plain GET (public/attendance.php, the
# `if (!$attendanceCaptchaEnabled && !$hasAttendedToday)` branch). So this needs no
# POST, no captcha and no browser -- just a logged-in cookie.
#
# Why this matters beyond the points: the newcomer exam (新人考核) has a 魔力增量
# requirement, and attendance streaks compound (+5 per consecutive day, +200 at day 10,
# +500 at 20, +1000 at 30). A missed day resets the streak, so unattended is the point.
#
# The cookie is password-equivalent and cannot be rotated without changing the account
# password, so it lives only in /etc/soulvoice-attend.env, root-only, never in the repo.
set -euo pipefail

ENV_FILE=${ENV_FILE:-/etc/soulvoice-attend.env}
SITE=${SITE:-https://pt.soulvoice.club}
ALERTMANAGER=${ALERTMANAGER:-http://10.10.204.35:9093}
UA=${UA:-"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/142.0.0.0 Safari/537.36"}
DRY_RUN=${DRY_RUN:-0}

if [ ! -r "$ENV_FILE" ]; then
  echo "missing $ENV_FILE (must define SOULVOICE_COOKIE)" >&2
  exit 78   # EX_CONFIG
fi
# shellcheck source=/dev/null
. "$ENV_FILE"
: "${SOULVOICE_COOKIE:?SOULVOICE_COOKIE not set in $ENV_FILE}"

WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT

CODE=$(curl -s -m 30 -A "$UA" -H "Cookie: $SOULVOICE_COOKIE" \
        "$SITE/attendance.php" -o "$WORK/att.html" -w "%{http_code}" || echo 000)

python3 - "$WORK/att.html" "$CODE" > "$WORK/verdict.json" <<'PY'
import sys, re, html, json

path, code = sys.argv[1], sys.argv[2]
try:
    raw = open(path, encoding="utf-8", errors="replace").read()
except Exception:
    raw = ""
body = re.sub(r"<(script|style).*?</\1>", "", raw, flags=re.S | re.I)
txt = re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", body)))

v = {"http": code, "state": "unknown", "detail": "", "exam": []}

# A dead cookie does not error - it silently serves the login page, which would
# otherwise look like a successful run forever.
if code != "200" or "login.php" in body and "退出" not in txt:
    v["state"] = "auth_failed"
elif "签到成功" in txt:
    v["state"] = "claimed"
elif any(k in txt for k in ("已签到", "今日已签到", "already")):
    v["state"] = "already"
else:
    v["state"] = "unclear"

m = re.search(r"本次签到获得\s*(\d+)\s*个魔力值", txt)
if m:
    v["detail"] = f"+{m.group(1)} bonus"
m = re.search(r"已连续签到\s*(\d+)\s*天", txt)
if m:
    v["streak"] = int(m.group(1))
m = re.search(r"魔力值\s*\[使用\]:\s*([\d.]+)", txt)
if m:
    v["bonus_total"] = m.group(1)

# 新人考核 - surface it, because failing it disables the account and the
# download requirement cannot be progressed at all during a freeleech event.
for mm in re.finditer(r"指标\d+：([^,]+?),\s*要求：([^,]+?),\s*当前：([^,]+?),\s*结果：(\S+?)！", txt):
    v["exam"].append({
        "metric": mm.group(1).strip(),
        "required": mm.group(2).strip(),
        "current": mm.group(3).strip(),
        "passed": mm.group(4).strip() != "未通过",
    })
json.dump(v, open(sys.stdout.fileno(), "w", closefd=False), ensure_ascii=False)
PY

STATE=$(python3 -c "import json;print(json.load(open('$WORK/verdict.json'))['state'])")
python3 - "$WORK/verdict.json" <<'PY'
import json, sys
v = json.load(open(sys.argv[1]))
print(f"state={v['state']} http={v['http']} {v.get('detail','')} "
      f"streak={v.get('streak','?')} bonus={v.get('bonus_total','?')}")
for e in v["exam"]:
    print(f"  exam {'PASS' if e['passed'] else 'FAIL'}  {e['metric']}: {e['current']} / {e['required']}")
PY

# Only page a human when something is actually wrong. A claimed or already-claimed
# day is the expected outcome and should stay silent.
if [ "$STATE" = "claimed" ] || [ "$STATE" = "already" ]; then
  exit 0
fi

python3 - "$WORK/verdict.json" "$WORK/alert.json" <<'PY'
import json, sys, datetime
v = json.load(open(sys.argv[1]))
now = datetime.datetime.now(datetime.timezone.utc)
reason = {
    "auth_failed": "the stored cookie is no longer valid - log in and refresh /etc/soulvoice-attend.env",
    "unclear": "attendance.php returned an unrecognised page - the site may have changed",
}.get(v["state"], v["state"])
json.dump([{
    "labels": {"alertname": "SoulVoiceAttendFailed", "severity": "warning",
               "service": "soulvoice", "instance": "pt.soulvoice.club"},
    "annotations": {
        "summary": f"SoulVoice daily attendance did not run ({v['state']}, HTTP {v['http']})",
        "description": reason + ". Streaks reset on a missed day, and attendance feeds the 新人考核 bonus requirement.",
    },
    "startsAt": now.isoformat(),
    "endsAt": (now + datetime.timedelta(hours=26)).isoformat(),
}], open(sys.argv[2], "w"), ensure_ascii=False)
PY

if [ "$DRY_RUN" = "1" ]; then
  echo "--- DRY_RUN, would POST: ---"; cat "$WORK/alert.json"; exit 1
fi

curl -sf -m 30 -X POST -H "Content-Type: application/json" \
  --data @"$WORK/alert.json" "$ALERTMANAGER/api/v2/alerts" >/dev/null \
  && echo "raised SoulVoiceAttendFailed on the bus" \
  || echo "failed to POST to Alertmanager" >&2
exit 1
