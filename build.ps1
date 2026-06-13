#!/usr/bin/env pwsh
# Homelab deployment pipeline entrypoint (Fallout build). Replaces the legacy
# Infrastructure/deploy/Deploy-Shape.ps1. Requires the .NET 10 SDK on PATH.
#
#   ./build.ps1                        # default target: ValidateShapes
#   ./build.ps1 Plan   --stack Core    # dry-run converge
#   ./build.ps1 Deploy --stack Core    # live apply
$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
& dotnet run --project "$ScriptDir/build/_build.csproj" -- $args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
