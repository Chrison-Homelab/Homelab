using System;
using System.IO;
using System.Linq;
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

    Target ValidateShapes => _ => _
        .Description("Validate every shape against shape.schema.json (engine `validate`).")
        .DependsOn(CompileEngine)
        .Executes(() =>
        {
            Engine($"validate {RootDirectory / "Infrastructure"}");
            Engine($"validate {StacksDirectory}");
        });

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
}
