using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Serilog;

// Homelab deployment pipeline — Fallout build (NUKE successor), per ADR-0001.
//
// Targets orchestrate the C# engine (Infrastructure/engine, `homelab-infra`):
//   ValidateShapes  → engine `validate` over Infrastructure/ + stacks/
//   Preview         → engine `converge <stack>`          (dry-run, no mutation)
//   Deploy          → engine `converge <stack> --apply`  (live)
//
// (Named "Preview" rather than "Plan" — FalloutBuild reserves the `--plan` flag,
// which prints the target graph.)
//
// "One pipeline per stack": Preview/Deploy take --stack <Name>; each stack gets its
// own generated GitHub Actions workflow (see Build.Ci.cs). The engine is invoked
// out-of-process so this build never needs the engine's package graph.
//
//   dotnet run --project build/_build.csproj -- Preview --stack Core
//   ./build.sh Deploy --stack Core
class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.ValidateShapes);

    [Parameter("Stack to plan/deploy — a directory name under stacks/ (e.g. 'Core').")]
    readonly string Stack;

    AbsolutePath StacksDirectory => RootDirectory / "stacks";
    AbsolutePath EngineProject => RootDirectory / "Infrastructure" / "engine";
    AbsolutePath EngineDll => EngineProject / "bin" / "Release" / "net10.0" / "homelab-infra.dll";
    AbsolutePath EngineTests => RootDirectory / "Infrastructure" / "engine.Tests";

    // Portable validator artifact (Phase 0 / ADR-0008): the SAME engine published as a
    // self-contained linux-x64 single-file binary + shape.schema.json beside it, so a
    // standalone stack repo's CI can run `homelab-infra validate <dir>` with no .NET SDK,
    // no private feeds and no self-hosted runner (published by publish-schema.yml).
    AbsolutePath ValidatorPublish => RootDirectory / "publish" / "validator";

    // PowerOrchestrator (tools/PowerOrchestrator, #191) — a long-running .NET service
    // deployed as a systemd unit ON the nuc-01 node, NOT a converge-able LXC/VM stack.
    // So it gets its own build/test/publish/deploy targets here rather than going
    // through ValidateShapes/Preview/Deploy. Fallout owns build→test→publish (native
    // dotnet); the node-side copy + systemd wiring is the deploy/deploy.sh "sugar".
    AbsolutePath PowerOrchestratorDir => RootDirectory / "tools" / "PowerOrchestrator";
    AbsolutePath PowerOrchestratorSln => PowerOrchestratorDir / "PowerOrchestrator.sln";
    AbsolutePath PowerOrchestratorService => PowerOrchestratorDir / "src" / "PowerOrchestrator.Service" / "PowerOrchestrator.Service.csproj";
    AbsolutePath PowerOrchestratorTests => PowerOrchestratorDir / "src" / "PowerOrchestrator.Tests" / "PowerOrchestrator.Tests.csproj";
    AbsolutePath PowerOrchestratorPublish => PowerOrchestratorDir / "publish";

    string[] DiscoverStacks() =>
        Directory.Exists(StacksDirectory)
            ? Directory.EnumerateDirectories(StacksDirectory)
                .Where(d => File.Exists(Path.Combine(d, "stack.yaml")))
                .Select(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : new string[0];

    AbsolutePath ResolveStack()
    {
        var available = string.Join(", ", DiscoverStacks());
        if (string.IsNullOrWhiteSpace(Stack))
            throw new Exception($"Missing --stack <name>. Available stacks: {available}");
        AbsolutePath dir = StacksDirectory / Stack;
        if (!File.Exists(dir / "stack.yaml"))
            throw new Exception($"Unknown stack '{Stack}' (no stacks/{Stack}/stack.yaml). Available: {available}");
        return dir;
    }

    // Invoke the built engine DLL out-of-process; non-zero exit fails the target.
    // NOTE: build the command into a plain `string` first. Passing an interpolated
    // string *directly* to StartProcess binds Fallout's ArgumentStringHandler, which
    // auto-quotes each interpolation hole — so a multi-token `{arguments}` would be
    // collapsed into one quoted argument. A pre-built string takes the plain overload.
    void Engine(string arguments)
    {
        string command = $"{EngineDll} {arguments}";
        ProcessTasks.StartProcess("dotnet", command, workingDirectory: RootDirectory).AssertZeroExitCode();
    }

    Target CompileEngine => _ => _
        .Description("Build the C# engine (Infrastructure/engine) that the deploy targets drive.")
        .Executes(() =>
            ProcessTasks
                .StartProcess("dotnet", $"build {EngineProject} -c Release --nologo", workingDirectory: RootDirectory)
                .AssertZeroExitCode());

    Target TestEngine => _ => _
        .Description("Run the engine unit tests (Infrastructure/engine.Tests) — the PR gate for the engine.")
        .DependsOn(CompileEngine)
        .Executes(() =>
        {
            // No --no-build: CompileEngine builds the ENGINE project only, not the test project
            // (unlike PowerOrchestrator, where CompilePowerOrchestrator builds the whole solution).
            var proc = ProcessTasks
                .StartProcess("dotnet", $"test {EngineTests} -c Release --nologo", workingDirectory: RootDirectory)
                .AssertZeroExitCode();

            // Guard against a SILENT pass: `dotnet test` exits 0 when it discovers nothing, so a
            // renamed/moved test project would turn the gate green while testing nothing at all.
            // Assert we actually ran tests (issue #299 asked for exactly this).
            var text = string.Join("\n", proc.Output.Select(o => o.Text));
            var match = Regex.Match(text, @"Total:\s*(\d+)");
            if (!match.Success || int.Parse(match.Groups[1].Value) == 0)
                throw new Exception(
                    $"TestEngine ran but discovered NO tests (expected the {nameof(EngineTests)} suite). " +
                    "A green run here would be meaningless — check the test project still exists and is discoverable.");

            Log.Information("Engine tests passed — {Total} test(s)", match.Groups[1].Value);
        });

    Target ValidateShapes => _ => _
        .Description("Validate every shape against shape.schema.json (engine `validate`).")
        .DependsOn(CompileEngine)
        .Executes(() =>
        {
            Engine($"validate {RootDirectory / "Infrastructure"}");
            Engine($"validate {StacksDirectory}");
        });

    Target PublishValidator => _ => _
        .Description("Publish the engine's validator as a self-contained linux-x64 single-file binary (+ schema beside) into publish/validator/ — the portable artifact for standalone stack-repo CI (ADR-0008).")
        .Executes(() =>
            ProcessTasks
                .StartProcess(
                    "dotnet",
                    $"publish {EngineProject} -c Release -r linux-x64 --self-contained true " +
                    $"-p:PublishSingleFile=true -p:DebugType=none -o {ValidatorPublish}",
                    workingDirectory: RootDirectory)
                .AssertZeroExitCode());

    Target Preview => _ => _
        .Description("Dry-run converge for --stack (diff desired vs live; no mutation).")
        .DependsOn(ValidateShapes)
        .Executes(() =>
        {
            var dir = ResolveStack();
            Log.Information("Previewing stack {Stack} (dry-run) — {Dir}", Stack, dir);
            Engine($"converge {dir}");
        });

    Target Deploy => _ => _
        .Description("Apply converge for --stack (LIVE mutation of the cluster).")
        .DependsOn(Preview)
        .Executes(() =>
        {
            var dir = ResolveStack();
            Log.Warning("Deploying stack {Stack} — LIVE apply against the cluster — {Dir}", Stack, dir);
            Engine($"converge {dir} --apply");
        });

    // ── PowerOrchestrator (#191) — build/test/publish/deploy the node service ──────

    Target CompilePowerOrchestrator => _ => _
        .Description("Build the PowerOrchestrator solution (tools/PowerOrchestrator).")
        .Executes(() =>
            ProcessTasks
                .StartProcess("dotnet", $"build {PowerOrchestratorSln} -c Release --nologo", workingDirectory: RootDirectory)
                .AssertZeroExitCode());

    Target TestPowerOrchestrator => _ => _
        .Description("Run PowerOrchestrator unit tests (policy/debounce, WoL, arm-guard, options).")
        .DependsOn(CompilePowerOrchestrator)
        .Executes(() =>
            ProcessTasks
                .StartProcess("dotnet", $"test {PowerOrchestratorTests} -c Release --no-build --nologo", workingDirectory: RootDirectory)
                .AssertZeroExitCode());

    Target PublishPowerOrchestrator => _ => _
        .Description("Publish PowerOrchestrator as a self-contained linux-x64 single-file binary into publish/.")
        .DependsOn(TestPowerOrchestrator)
        .Executes(() =>
            ProcessTasks
                .StartProcess(
                    "dotnet",
                    $"publish {PowerOrchestratorService} -c Release -r linux-x64 --self-contained true " +
                    $"-p:PublishSingleFile=true -p:DebugType=none -o {PowerOrchestratorPublish}",
                    workingDirectory: RootDirectory)
                .AssertZeroExitCode());

    Target DeployPowerOrchestrator => _ => _
        .Description("Deploy PowerOrchestrator to nuc-01: publish, then copy + systemd via deploy/deploy.sh.")
        .DependsOn(PublishPowerOrchestrator)
        .Executes(() =>
        {
            // Fallout published into publish/; the script just copies it onto the node
            // and wires up the systemd unit + EnvironmentFile. Pre-built string so the
            // plain StartProcess overload is used (no ArgumentStringHandler quoting).
            string script = PowerOrchestratorDir / "deploy" / "deploy.sh";
            Log.Warning("Deploying PowerOrchestrator to the node — copy + systemd — {Script}", script);
            ProcessTasks.StartProcess("bash", script, workingDirectory: RootDirectory).AssertZeroExitCode();
        });
}
