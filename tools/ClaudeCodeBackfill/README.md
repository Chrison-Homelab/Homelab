# Claude Code transcript backfill

Turns `~/.claude/projects/**/*.jsonl` into Prometheus TSDB blocks so the Claude Code
usage dashboard has history from before OTel telemetry was switched on.

```
BACKFILL_INSTANCE=<service.instance.id> python3 generate.py /tmp/cc [live-sessions.txt] [cutoff-epoch]
```

`BACKFILL_INSTANCE` is required and deliberately not guessed. It must match, exactly, the
`service.instance.id` in that machine's `~/.claude/settings.json` — Prometheus stores it as
`exported_instance` and the dashboard groups on it. A default of "local hostname" would be
the silent-wrong-data failure: settings saying `MFB-1234` and a backfill saying
`MFB-1234.local` produce two machines each holding half the history, with nothing erroring.
`BACKFILL_EMAIL` is optional and adds the `user_email` label.

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


## A second machine (the Windows work laptop)

Generation needs the transcripts; loading needs the homelab. Those are different machines,
so split them — do not copy transcripts onto another box.

1. **On the laptop**, set `service.instance.id` in `~/.claude/settings.json` first (replace
   `REPLACE-WITH-LAPTOP-HOSTNAME`). Backfill and live telemetry must agree on it or the
   dashboard shows the same laptop twice.
2. **On the laptop**, run the generator with that same value. It is pure stdlib Python and
   reads `%USERPROFILE%\.claude\projects`; output is forced to UTF-8 and LF, because
   Windows would otherwise write CRLF that the OpenMetrics parser rejects.
3. **Copy the two `.om` files** to a machine with homelab access and load them there, as in
   *Loading* above.

Notes for a much bigger history:

* **Blocks scale with span.** `--max-block-duration=24h` over a year is ~365 blocks; use
  `--max-block-duration=168h` for long spans. Prometheus wants a block duration under 10% of
  retention, so with 400d anything up to ~40d is fine.
* **Check the oldest transcript against retention.** Samples older than
  `--storage.tsdb.retention.time` are deleted on the next compaction — silently, as
  "obsolete block". The generator prints its span; if it starts earlier than retention
  allows, raise retention *before* loading or that history evaporates.
* **Only counts leave the machine.** Token totals, session ids, model names and timestamps —
  no prompt or response text, no file paths, no project names. Worth knowing when the source
  is a work laptop.
