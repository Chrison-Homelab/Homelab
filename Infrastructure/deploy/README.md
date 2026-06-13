# Infrastructure/deploy — retired (moved to the Fallout pipeline)

`Deploy-Shape.ps1` (the PowerShell community-scripts renderer, BL-013) has been
**removed**. Deployment is now a **Fallout build pipeline** that drives the C#
engine — one target chain per stack. See [ADR-0001](../../docs/adr/ADR-0001-iac-tooling.md)
("the engine runs as a Fallout build target").

## Where it went

| Was (PowerShell) | Now |
|---|---|
| `Deploy-Shape.ps1 -ShapePath x.yaml` (dry-run) | `./build.sh Preview --stack <Name>` |
| `Deploy-Shape.ps1 -ShapePath x.yaml -Apply` | `./build.sh Deploy  --stack <Name>` |
| shape → `var_*` rendering + SSH run | engine `converge` (`Infrastructure/engine/Converge/CommunityScriptsCreator.cs`) |
| `tools/validate-shapes.py` | `./build.sh ValidateShapes` (engine `validate`) |

- **Pipeline:** [`build/`](../../build) — `Build.cs` targets (`ValidateShapes`,
  `Preview`, `Deploy`), bootstrapped by `./build.sh` / `./build.ps1`.
- **Per-stack CI:** `.github/workflows/deploy-<stack>.yml` (one per stack) →
  `_deploy-stack.yml`. PR previews; manual dispatch applies.
- **Engine:** [`Infrastructure/engine/`](../engine) still owns the shape contract,
  community-scripts create, `pct` config reconcile, and lifecycle — the Fallout
  targets just orchestrate it. The shape schema is unchanged.
