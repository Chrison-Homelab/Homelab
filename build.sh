#!/usr/bin/env bash
# Homelab deployment pipeline entrypoint (Fallout build). Replaces the legacy
# Infrastructure/deploy/Deploy-Shape.ps1. Requires the .NET 10 SDK on PATH
# (the homelab runner + dev boxes already have it; see global.json).
#
#   ./build.sh                        # default target: ValidateShapes
#   ./build.sh Plan   --stack Core    # dry-run converge
#   ./build.sh Deploy --stack Core    # live apply
set -eo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project "$SCRIPT_DIR/build/_build.csproj" -- "$@"
