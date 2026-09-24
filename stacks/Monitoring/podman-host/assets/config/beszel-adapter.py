#!/usr/bin/env python3
"""Beszel -> Alertmanager adapter (#590).

Beszel's generic webhook can only emit a FLAT JSON object:

    {"title": "...", "message": "..."}

Alertmanager's /api/v2/alerts requires an ARRAY of objects with nested `labels` and
`annotations`. No combination of shoutrrr query parameters bridges that, and Beszel's own
docs say so ("use of an intermediate proxy to modify the payload"). Hence this.

Deliberately stdlib-only and mounted as an asset onto a stock python image: no bespoke
image to build, publish or keep patched, and it is read directly from the repo.

── HOW IT DECIDES severity AND firing/resolved ─────────────────────────────────────────
Two paths, in order of preference:

  1. STRUCTURED (preferred). Beszel supports per-alert-type notification templates. Set
     the title template to emit a delimited record:

         beszel|{status}|{system}|{alert-type}

     and everything below is read as fields. Deterministic.

  2. PROSE FALLBACK. If the title is not in that form, the text is pattern-matched for
     "down"/"offline"/"resolved". This WORKS BUT IS FRAGILE -- it breaks silently the day
     Beszel rewords a string in a point release. Every use of this path is logged as a
     warning so the fallback is visible rather than quietly load-bearing.

The fallback exists so the adapter is useful the moment it is deployed, before templates
are configured -- not as the intended steady state.
"""
import json
import logging
import os
import re
import urllib.error
import urllib.request
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer

ALERTMANAGER = os.environ.get("ALERTMANAGER_URL", "http://alertmanager:9093/api/v2/alerts")
LISTEN_PORT = int(os.environ.get("LISTEN_PORT", "9099"))
# Routes to the `monitoring` ntfy topic via the route that already exists in
# alertmanager.yml. Nothing downstream needs changing for this to be delivered.
STACK = os.environ.get("ALERT_STACK", "monitoring")
MAX_BODY = 64 * 1024

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("beszel-adapter")

# "down" covers a host that stopped reporting -- the one Beszel condition worth waking
# someone for, so it is the only one mapped to critical (ntfy priority 5, bypasses Focus).
DOWN = re.compile(r"\b(down|offline|unreachable)\b", re.I)
RESOLVED = re.compile(r"\b(resolved|recovered|back online|up again)\b", re.I)


def parse(payload):
    """-> (alertname, system, severity, status, used_fallback)"""
    title = (payload.get("title") or "").strip()
    message = (payload.get("message") or "").strip()

    # Path 1: structured title.
    if title.lower().startswith("beszel|"):
        parts = [p.strip() for p in title.split("|")]
        # beszel | status | system | alert-type
        status = (parts[1] if len(parts) > 1 else "firing").lower()
        system = parts[2] if len(parts) > 2 else "unknown"
        atype = parts[3] if len(parts) > 3 else "BeszelAlert"
        status = "resolved" if status in ("resolved", "ok", "up") else "firing"
        severity = "critical" if DOWN.search(atype) or atype.lower() == "status" else "warning"
        return atype or "BeszelAlert", system, severity, status, False

    # Path 2: prose fallback.
    haystack = f"{title} {message}"
    status = "resolved" if RESOLVED.search(haystack) else "firing"
    severity = "critical" if DOWN.search(haystack) else "warning"
    # Beszel titles read "<system> <metric> above threshold"; first token is the system.
    system = title.split()[0] if title else "unknown"
    return "BeszelAlert", system, severity, status, True


def build(payload):
    alertname, system, severity, status, fallback = parse(payload)
    if fallback:
        log.warning(
            "prose fallback used -- notification templates are not set; "
            "severity/status were inferred from text, not read. title=%r",
            payload.get("title"),
        )
    alert = {
        "labels": {
            "alertname": alertname,
            # Alertmanager groups by [alertname, name]; `name` is the thing the alert is
            # about, so one flapping host is one notification.
            "name": system,
            "severity": severity,
            "stack": STACK,
            "source": "beszel",
        },
        "annotations": {
            "summary": payload.get("title") or "Beszel alert",
            "description": payload.get("message") or "",
        },
    }
    if status == "resolved":
        # An endsAt in the past resolves it immediately rather than waiting out
        # resolve_timeout, so the recovery lands at ntfy priority 2 promptly.
        alert["endsAt"] = datetime.now(timezone.utc).isoformat()
    return [alert]


def forward(alerts):
    body = json.dumps(alerts).encode()
    req = urllib.request.Request(
        ALERTMANAGER, data=body, headers={"Content-Type": "application/json"}, method="POST"
    )
    with urllib.request.urlopen(req, timeout=10) as resp:
        return resp.status


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        try:
            length = int(self.headers.get("Content-Length") or 0)
            if length <= 0 or length > MAX_BODY:
                return self.reply(400, "bad content-length")
            payload = json.loads(self.rfile.read(length))
            if not isinstance(payload, dict):
                return self.reply(400, "expected a JSON object")
        except Exception as exc:
            log.error("could not read payload: %s", exc)
            return self.reply(400, "bad payload")

        try:
            alerts = build(payload)
            status = forward(alerts)
        except urllib.error.URLError as exc:
            # Say this loudly: a silent failure here means alerts stop reaching the phone
            # while Beszel's own UI still shows them as sent.
            log.error("FORWARD FAILED -> %s: %s", ALERTMANAGER, exc)
            return self.reply(502, "alertmanager unreachable")
        except Exception as exc:
            log.error("unexpected failure: %s", exc)
            return self.reply(500, "error")

        log.info(
            "forwarded %s/%s severity=%s -> %s",
            alerts[0]["labels"]["alertname"],
            alerts[0]["labels"]["name"],
            alerts[0]["labels"]["severity"],
            status,
        )
        self.reply(204, "")

    def do_GET(self):
        # Liveness only; nothing here is authenticated because it is reachable on the
        # container network alone and is never published.
        self.reply(200, "ok") if self.path == "/healthz" else self.reply(404, "not found")

    def reply(self, code, text):
        data = text.encode()
        self.send_response(code)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        if data:
            self.wfile.write(data)

    def log_message(self, *args):
        pass  # handled by the logger above


if __name__ == "__main__":
    log.info("listening on :%s -> %s (stack=%s)", LISTEN_PORT, ALERTMANAGER, STACK)
    HTTPServer(("0.0.0.0", LISTEN_PORT), Handler).serve_forever()
