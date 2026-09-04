#!/usr/bin/env python3
"""Turn Claude Code transcripts into OpenMetrics for promtool tsdb backfill.

Emits cumulative-per-(session,model,type) counters matching the live OTel metric
shape, so max_over_time() gives session totals and increase() gives real rates.

Two things this gets right that a naive reading does not:
  * One API response writes SEVERAL assistant records (one per content block),
    each repeating the SAME message.usage. Deduping on message.id is mandatory --
    summing raw records over-counts by ~2.2x.
  * Subagent transcripts live in subagents/*.jsonl and carry the PARENT sessionId
    with isSidechain=true; they are absent from the parent file, so both must be
    read and neither double-counts.

!! CUTOFF -- READ BEFORE CHANGING !!
Samples newer than the last completed 2h block boundary are DROPPED, and this is not
cosmetic. A backfilled block whose maxTime reaches into Prometheus's head makes the next
restart set the head's min-valid-time to that block's maxTime and SILENTLY DISCARD every
older WAL sample. WAL replay still logs "completed" and no counter moves. Doing exactly
this cost ~2h of all homelab metrics on 2026-09-05, because the transcript of the session
doing the backfill ran up to the present minute.

usage: generate.py <out-prefix> [exclude-sessions-file] [cutoff-epoch]
"""
import json, os, sys, glob, datetime, collections

ROOT = os.path.expanduser("~/.claude/projects")
HOST = "Christians-MacBook-Air"
EMAIL = "chris.simon@myfoodbag.co.nz"

exclude = set()
if len(sys.argv) > 2 and sys.argv[2] not in ("", "-"):
    exclude = {l.strip() for l in open(sys.argv[2]) if l.strip()}

# default: last completed 2h boundary, which is always below the running head's minTime
import time as _time
CUTOFF = float(sys.argv[3]) if len(sys.argv) > 3 else (int(_time.time()) // 7200) * 7200

FIELDS = {"input": "input_tokens", "output": "output_tokens",
          "cacheCreation": "cache_creation_input_tokens",
          "cacheRead": "cache_read_input_tokens"}

def epoch(ts):
    return datetime.datetime.strptime(ts, "%Y-%m-%dT%H:%M:%S.%fZ").replace(
        tzinfo=datetime.timezone.utc).timestamp()

seen_msg = set()
events = []                      # (t, session, model, type, value)
sess_first = {}                  # session -> earliest t
files = sorted(glob.glob(os.path.join(ROOT, "**", "*.jsonl"), recursive=True))
skipped_sessions = collections.Counter()
after_cutoff = 0
bad = 0

for f in files:
    for line in open(f, errors="replace"):
        try:
            d = json.loads(line)
        except Exception:
            bad += 1
            continue
        if d.get("type") != "assistant":
            continue
        m = d.get("message") or {}
        u = m.get("usage")
        if not isinstance(u, dict):
            continue
        sid = d.get("sessionId") or d.get("session_id")
        ts = d.get("timestamp")
        if not sid or not ts:
            bad += 1
            continue
        if sid in exclude:
            skipped_sessions[sid] += 1
            continue
        mid = m.get("id")
        if mid:
            if mid in seen_msg:
                continue
            seen_msg.add(mid)
        try:
            t = epoch(ts)
        except Exception:
            bad += 1
            continue
        if t >= CUTOFF:
            after_cutoff += 1
            continue
        model = m.get("model") or "unknown"
        sess_first[sid] = min(sess_first.get(sid, t), t)
        for typ, key in FIELDS.items():
            v = u.get(key) or 0
            if v:
                events.append((t, sid, model, typ, v))

events.sort(key=lambda e: e[0])

# accumulate into cumulative counters, one series per (session, model, type)
running = collections.defaultdict(int)
last_t = {}
samples = []
for t, sid, model, typ, v in events:
    k = (sid, model, typ)
    running[k] += v
    # Prometheus rejects duplicate timestamps on a series; nudge forward instead
    if k in last_t and t <= last_t[k]:
        t = last_t[k] + 0.001
    last_t[k] = t
    samples.append((t, k, running[k]))

def lbl(**kw):
    return ",".join('%s="%s"' % (k, v) for k, v in sorted(kw.items()))

COMMON = dict(exported_instance=HOST, exported_job="claude-code",
              instance="otel-collector:8889", job="otel-collector",
              otel_scope_name="com.anthropic.claude_code",
              service_instance_id=HOST, user_email=EMAIL,
              backfill="transcripts")

out = sys.argv[1]
with open(out + ".tokens.om", "w") as fh:
    fh.write("# TYPE claude_code_token_usage_tokens counter\n")
    for t, (sid, model, typ), v in samples:
        fh.write("claude_code_token_usage_tokens_total{%s} %d %.3f\n"
                 % (lbl(session_id=sid, model=model, type=typ, **COMMON), v, t))
    fh.write("# EOF\n")

with open(out + ".sessions.om", "w") as fh:
    fh.write("# TYPE claude_code_session_count counter\n")
    for sid, t in sorted(sess_first.items(), key=lambda x: x[1]):
        fh.write("claude_code_session_count_total{%s} 1 %.3f\n"
                 % (lbl(session_id=sid, start_type="fresh", **COMMON), t))
    fh.write("# EOF\n")

tot = collections.Counter()
for k, v in running.items():
    tot[k[2]] += v
span = (min(s[0] for s in samples), max(s[0] for s in samples))
fmt = lambda x: datetime.datetime.fromtimestamp(x, datetime.timezone.utc).strftime("%Y-%m-%d %H:%M")
print("cutoff           %s UTC  (samples at/after: %d dropped)"
      % (datetime.datetime.fromtimestamp(CUTOFF, datetime.timezone.utc).strftime("%Y-%m-%d %H:%M"),
         after_cutoff))
print("files            %d  (parse failures: %d)" % (len(files), bad))
print("unique messages  %d" % len(seen_msg))
print("sessions         %d   (excluded as already-live: %d)"
      % (len(sess_first), len(skipped_sessions)))
print("series           %d" % len(running))
print("samples          %d" % len(samples))
print("span             %s .. %s UTC" % (fmt(span[0]), fmt(span[1])))
for k in ("input", "output", "cacheCreation", "cacheRead"):
    print("  %-14s %15s" % (k, "{:,}".format(tot[k])))
print("  %-14s %15s" % ("TOTAL", "{:,}".format(sum(tot.values()))))
