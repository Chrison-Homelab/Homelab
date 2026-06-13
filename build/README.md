# build/ — Fallout deployment pipeline

The homelab's deployment pipeline, built on [Fallout](https://github.com/Fallout-build/Fallout)
(Chris's C#/.NET build system, a NUKE successor). Implements ADR-0001's "the
engine runs as a Fallout build target"; **replaces** `Infrastructure/deploy/Deploy-Shape.ps1`.

The targets orchestrate the C# engine (`Infrastructure/engine`) — they don't
re-implement deploy logic. The engine is invoked out-of-process, so this build
never pulls the engine's package graph.

## Targets

| Target | Does |
|---|---|
| `ValidateShapes` | engine `validate` over `Infrastructure/` + `stacks/` (replaces `tools/validate-shapes.py`) |
| `Preview` | engine `converge <stack>` — dry-run; diffs desired vs live, no mutation |
| `Deploy` | engine `converge <stack> --apply` — live |

`Preview`/`Deploy` require `--stack <Name>` (a directory under `stacks/`).

## Run

```bash
./build.sh                         # default: ValidateShapes
./build.sh Preview --stack Core    # dry-run a stack
./build.sh Deploy  --stack Core    # live apply
# equivalently: dotnet run --project build/_build.csproj -- <Target> [--stack X]
```

Requires the .NET 10 SDK (see `../global.json`) and `GITHUB_PACKAGES_PAT` in the
environment — Fallout restores from the Fallout-build GitHub Packages edge feed
(`../nuget.config`), and the engine restores ProxmoxSharp/UnifiSharp from the
chrison-dev feed. A live `Deploy` also needs Proxmox API creds, `CF_API_TOKEN`,
and SSH to the target node (all per `secrets.env`).

## CI

One pipeline per stack: `.github/workflows/deploy-<stack>.yml` → the reusable
`_deploy-stack.yml`. A PR touching a stack runs `Preview`; a manual
`workflow_dispatch` with `apply=true` runs `Deploy`. All on the `[self-hosted,
homelab]` runner (needs LAN + the package feeds).

## Versioning

Fallout packages are pinned in `_build.csproj` to `2026.1.0-preview.5…` — the
highest version where `fallout.cli` and the build libs are all published (the
CLI lags the libs). Bump deliberately.
