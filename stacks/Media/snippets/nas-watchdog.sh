#!/usr/bin/env bash
# nas-watchdog (ITERATION 1 — quick & dirty) — runtime guard for NAS-backed media CTs.
#
# The boot-time rootfs-fill (CT 5007) is handled STRUCTURALLY elsewhere (immutable
# underlying mountpoint + the ensure-data-mount.sh pre-start hookscript). This watchdog
# handles the *runtime* case: if the NAS goes away while the app is running, a hard NFS
# mount leaves the app's IO wedged (D-state). We detect the loss and STOP the app cleanly,
# then START it again when the NAS returns — turning an indefinite hang into something
# visible and self-healing.
#
# Per-CT install (one of the 4 file-touching members): this script at
# /usr/local/sbin/nas-watchdog.sh, the .service + .timer in /etc/systemd/system/, and a
# per-CT /etc/nas-watchdog.conf setting SERVICE= (the app's systemd unit). Run by the timer.
#
# FIRST ITERATION ONLY. The planned v2 is a C# host service + in-LXC agents executing
# configured actions (qBittorrent API pause, kill/start, etc.) — see the tracking issue.
# Known limitation: a process already wedged in uninterruptible IO on a hard mount may not
# die until the NAS returns; `systemctl stop` is best-effort (backgrounded) here.
set -uo pipefail

CONF=/etc/nas-watchdog.conf
[ -r "$CONF" ] && . "$CONF"
MOUNT="${MOUNT:-/data}"
SERVICE="${SERVICE:?nas-watchdog: set SERVICE= in $CONF (the app's systemd unit)}"
PROBE_TIMEOUT="${PROBE_TIMEOUT:-5}"
DOWN_FLAG=/run/nas-watchdog.down

# Liveness: must be a real mountpoint AND answer a statfs within the timeout. The timeout is
# the whole point — a naked stat on a hung hard mount blocks forever.
nas_alive() {
  timeout "$PROBE_TIMEOUT" mountpoint -q "$MOUNT" 2>/dev/null || return 1
  timeout "$PROBE_TIMEOUT" stat -f "$MOUNT" >/dev/null 2>&1 || return 1
  return 0
}

if nas_alive; then
  if [ -e "$DOWN_FLAG" ]; then
    logger -t nas-watchdog "NAS back at $MOUNT — starting $SERVICE"
    rm -f "$DOWN_FLAG"
    systemctl start "$SERVICE"
  fi
else
  if [ ! -e "$DOWN_FLAG" ]; then
    logger -t nas-watchdog "NAS unreachable at $MOUNT — stopping $SERVICE to avoid an IO-wedged app"
    : > "$DOWN_FLAG"
    systemctl stop "$SERVICE" &   # backgrounded: stop may block if the app is wedged in D-state
  fi
fi
exit 0
