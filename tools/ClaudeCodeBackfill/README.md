# Claude Code transcript backfill

Turns `~/.claude/projects/**/*.jsonl` into Prometheus TSDB blocks so the Claude Code
usage dashboard has history from before OTel telemetry was switched on.

```
python3 generate.py /tmp/cc [live-sessions.txt] [cutoff-epoch]
```

Writes `<prefix>.tokens.om` and `<prefix>.sessions.om` in OpenMetrics, as
cumulative-per-`(session, model, type)` counters over each session's real timeline — so
`max_over_time()` gives session totals and `increase()` gives genuine rates, unlike live
one-shot `claude -p` runs whose series are flat.

Pass the session ids already present in Prometheus as the second argument, or they will
be counted twice:

```
curl -sG http://monitoring.homelab.chrison.internal:9091/api/v1/series \
  --data-urlencode 'match[]={__name__=~"claude_code.*"}' \
  | python3 -c 'import json,sys;print("\n".join(sorted({x["session_id"] for x in json.load(sys.stdin)["data"] if x.get("session_id")})))' \
  > live-sessions.txt
```

## ⚠ The cutoff is load-bearing

Samples at or after the last completed 2h block boundary are dropped on purpose.

A block whose `maxTime` reaches into Prometheus's head makes the next restart set the
head's min-valid-time to that `maxTime` and **silently discard every older WAL sample**.
WAL replay still logs `"WAL replay completed"`, no error is raised, and
`prometheus_tsdb_out_of_bound_samples_total` stays `0`.

On 2026-09-05 this destroyed ~2h of *all* homelab metrics: the transcript of the session
running the backfill extended to the present minute, so the blocks ended at 09:59:47 and
took the live head with them.

## Loading

promtool lives in the prometheus container; CT 4001 on hpe-01 is the monitoring host.

```
pct push 4001 cc.tokens.om /home/podman/monitoring/data/prometheus/backfill/cc.tokens.om
podman exec prometheus promtool tsdb create-blocks-from openmetrics \
    --max-block-duration=24h /prometheus/backfill/cc.tokens.om /prometheus/backfill/out
mv .../backfill/out/* .../prometheus/ && chown -R podman:podman .../prometheus
systemctl --user restart prometheus
```

Run the two families separately — OpenMetrics forbids interleaved metric families, and
each file must be globally timestamp-sorted.

## Verifying, and recovering if it goes wrong

**Probe fixed timestamps per metric.** A `query_range` gap check reported `gaps: none`
across a window that was entirely missing; it is not a valid check.

```
curl -sG .../api/v1/query --data-urlencode 'query=count(up)' --data-urlencode "time=$TS"
```

If the head was eaten, the WAL segments are still on disk — the samples were dropped
from memory, not deleted:

1. stop prometheus, `mv` the offending block (the only one whose `maxTime` exceeds the
   head's `minTime`) out of the data dir, start prometheus — WAL replay re-admits them;
2. restore the block **only** once the head has been compacted into a block reaching
   past that block's `maxTime`, or `reloadBlocks()` truncates the head again at runtime.
