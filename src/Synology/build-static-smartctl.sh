#!/usr/bin/env bash
# build-static-smartctl.sh
#
# Builds a STATICALLY LINKED smartctl for the Synology NAS, from the official
# upstream tarball, and verifies it against the publisher's own checksum.
#
# ── WHY THIS EXISTS ─────────────────────────────────────────────────────────────
# DSM 7.1.1 ships smartctl 6.5 (2021). The Pulse agent collects S.M.A.R.T. by running
#     smartctl -n standby,3 -i -A -H --json=o /dev/sdX
# and JSON output only arrived in smartmontools 7.0 — 6.5 ignores --json, prints its
# banner and exits. The agent parses nothing, so every disk shows health=UNKNOWN and
# temperature=0 in Pulse. That is the single most valuable thing an agent on a NAS is
# for, so it is worth fixing.
#
# The agent honours PULSE_SMARTCTL_PATH, so it only needs a modern binary somewhere on
# disk — no need to touch DSM's own /usr/bin/smartctl, which stays exactly as shipped.
#
# ── WHY BUILD RATHER THAN DOWNLOAD ──────────────────────────────────────────────
# The alternatives are Entware (bootstraps a whole third-party package tree onto the
# appliance) or someone's prebuilt binary (unknown provenance, running as root against
# raw block devices). Building from the signed-by-checksum upstream tarball in a
# throwaway container gives a binary whose entire history is auditable from this file.
#
# STATIC on purpose: DSM 7.1 is glibc ~2.20 on kernel 3.10. A dynamically linked build
# from any current distro will not load there. A static musl binary has no such coupling.
#
# ── USAGE ───────────────────────────────────────────────────────────────────────
#   ./build-static-smartctl.sh                    # build → ./smartctl-7 (+ checksum)
#   ./build-static-smartctl.sh --out /tmp/sc      # custom output path
#   ./build-static-smartctl.sh --version 7.5      # pin a different upstream release
#
# Then copy it to the NAS. `scp` fails on DSM when the account has no home directory,
# so pipe it over stdin, and note /tmp is mounted noexec — it has to be moved into
# place before it will run:
#
#   NAS=homelab@nas.homelab.chrison.internal
#   cat smartctl-7 | ssh "$NAS" 'cat > /tmp/smartctl-7'
#   ssh -t "$NAS" 'sudo sh -c "mv /tmp/smartctl-7 /usr/local/bin/smartctl-7 \
#                              && chown root:root /usr/local/bin/smartctl-7 \
#                              && chmod 755 /usr/local/bin/smartctl-7"'
#
# install-pulse-agent.sh then detects it and points the agent at it automatically.
#
# Requirements: Docker (the build runs in a throwaway Alpine container; nothing is
# installed on this machine).

set -euo pipefail

VERSION="7.5"
OUT="./smartctl-7"

# Publisher's MD5, from smartmontools-<ver>.tar.gz.md5 on SourceForge. Checked against
# the tarball before a single line is compiled. Add a case when pinning a new release.
# A case, not an associative array: macOS still ships bash 3.2, which has no `declare -A`.
upstream_md5() {
    case "$1" in
        7.5) echo "38c38b0b82db7fc4906cdd50d15a7931" ;;
        *)   echo "" ;;
    esac
}

usage() { sed -n '2,45p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

log() { printf '\033[0;36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[0;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --out)     OUT="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        -h|--help) usage 0 ;;
        *) echo "Unknown option: $1" >&2; usage 1 ;;
    esac
done

command -v docker >/dev/null 2>&1 || die "docker is required (the build runs in a container)."
docker info >/dev/null 2>&1 || die "the docker daemon is not running."

EXPECTED_MD5="$(upstream_md5 "$VERSION")"
[ -n "$EXPECTED_MD5" ] || die "No pinned upstream MD5 for smartmontools $VERSION.
       Fetch smartmontools-${VERSION}.tar.gz.md5 from SourceForge and add it to UPSTREAM_MD5."

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/build.sh" <<BUILD
#!/bin/sh
set -eu
VER="$VERSION"
EXPECTED="$EXPECTED_MD5"
apk add --no-cache build-base curl tar gzip linux-headers binutils >/dev/null 2>&1
cd /tmp
curl -fsSL -o s.tar.gz "https://downloads.sourceforge.net/project/smartmontools/smartmontools/\${VER}/smartmontools-\${VER}.tar.gz"

# Verify BEFORE unpacking or compiling anything.
ACTUAL="\$(md5sum s.tar.gz | cut -d' ' -f1)"
if [ "\$ACTUAL" != "\$EXPECTED" ]; then
    echo "CHECKSUM MISMATCH: expected \$EXPECTED, got \$ACTUAL" >&2
    exit 1
fi
echo "tarball md5 verified: \$ACTUAL"

tar xzf s.tar.gz && cd "smartmontools-\${VER}"
./configure --disable-nls --without-selinux --without-libcap-ng \\
    --without-systemdsystemunitdir --without-update-smart-drivedb \\
    LDFLAGS="-static" >/dev/null 2>&1
make -j"\$(nproc)" smartctl >/dev/null 2>&1
strip smartctl
cp smartctl /out/smartctl-7
echo "built: \$(/out/smartctl-7 --version | head -1)"
echo "binary md5: \$(md5sum /out/smartctl-7 | cut -d' ' -f1)"
BUILD

log "Building smartmontools $VERSION (static, linux/amd64) in a throwaway container"
docker run --rm --platform linux/amd64 \
    -v "$WORK:/out" -v "$WORK/build.sh:/build.sh:ro" \
    alpine:3.20 sh /build.sh || die "build failed"

[ -f "$WORK/smartctl-7" ] || die "build produced no binary"
mkdir -p "$(dirname "$OUT")"
cp "$WORK/smartctl-7" "$OUT"
chmod 755 "$OUT"

log "Wrote $OUT"
log "Copy it to the NAS with the commands in this script's header (--help)."
