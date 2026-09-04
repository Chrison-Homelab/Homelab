using System.Text;
using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Dashboard;

// `homelab-infra dashboard <stacks-dir> [--out <file>] [--check] [--deploy]` (ADR-0012).
//
//   render   every stack under <stacks-dir> → Homepage services.yaml on stdout (or --out).
//   --check  exit 3 if a public route has no declaring shape, or a widget names a secret the
//            dashboard host does not export. Runs AFTER --deploy so a gap is visible but never
//            blocks the dashboard from updating.
//   --deploy push the render to the dashboard host — the ONE shape across the stacks carrying
//            `config.dashboard: { services: <rel>, unit: <unit> }` — into <assetsTarget>/<rel>,
//            and restart <unit> as the podman user, ONLY if the content changed (sha256 compared
//            first). Needs the same node SSH access converge uses; no other credentials.
//
// Deliberately NOT part of converge: converging the Monitoring host restarts every unit on it,
// and declaring a new app elsewhere must not blind monitoring. This is a one-file push.
public static class DashboardCommand
{
    public static async Task<int> RunAsync(string[] args, INodeExec exec, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2)
        {
            stderr.WriteLine("usage: homelab-infra dashboard <stacks-dir> [--out <file>] [--check] [--deploy]");
            return 2;
        }
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root)) { stderr.WriteLine($"directory not found: {root}"); return 2; }
        string? outPath = null;
        var check = false; var deploy = false;
        for (var i = 2; i < args.Length; i++)
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--check": check = true; break;
                case "--deploy": deploy = true; break;
                default: stderr.WriteLine($"unknown argument '{args[i]}'"); return 2;
            }

        var stacks = LoadAllStacks(root, stderr);
        var model = HomepageDashboard.Build(stacks);
        var yaml = HomepageDashboard.Render(model);

        var host = FindDashboardHost(stacks);
        if (outPath is not null) { File.WriteAllText(outPath, yaml); stderr.WriteLine($"rendered {model.Entries.Count} service(s) → {outPath}"); }
        else if (!deploy) stdout.Write(yaml);

        if (deploy)
        {
            if (host is null) { stderr.WriteLine("no shape declares config.dashboard — nowhere to deploy"); return 2; }
            var (msg, failed) = await DeployAsync(host, yaml, exec);
            if (failed is not null) { stderr.WriteLine($"deploy failed: {failed}"); return 1; }
            stdout.WriteLine(msg);
        }

        if (check)
        {
            var exported = host is null ? Array.Empty<string>() : ExportedVarsOf(host);
            var problems = HomepageDashboard.Check(model, exported);
            if (problems.Count > 0)
            {
                stderr.WriteLine($"dashboard check: {problems.Count} problem(s)");
                foreach (var p in problems) stderr.WriteLine($"  ! {p}");
                return 3;
            }
            stderr.WriteLine($"dashboard check: OK — {model.Entries.Count} service(s), every public route declared, every widget secret exported");
        }
        return 0;
    }

    // Every stack dir under the root (the superproject's stacks/, submodules included when
    // checked out). Lenient variables: this reads metadata only, and runs where secrets.env
    // may not exist. A stack that fails to load is REPORTED and fatal — a dashboard rendered
    // from half the stacks would look complete.
    internal static List<(string StackName, ShapeLoader.LoadedStack Stack)> LoadAllStacks(string root, TextWriter stderr)
    {
        var dirs = Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "stack.yaml")) || Directory.EnumerateFiles(d, "*.lxc.yaml").Any())
            .OrderBy(d => d, StringComparer.Ordinal).ToList();
        var result = new List<(string, ShapeLoader.LoadedStack)>();
        var failures = 0;
        foreach (var dir in dirs)
        {
            try
            {
                var loaded = ShapeLoader.LoadStack(dir, null, lenientVars: true);
                result.Add((loaded.Stack?.Metadata.Name ?? Path.GetFileName(dir), loaded));
            }
            catch (Exception ex) { failures++; stderr.WriteLine($"  ! {Path.GetFileName(dir)}: {ex.Message}"); }
        }
        if (failures > 0) throw new InvalidOperationException($"{failures} stack(s) failed to load — refusing to render a partial dashboard");
        return result;
    }

    internal static Shape? FindDashboardHost(IEnumerable<(string StackName, ShapeLoader.LoadedStack Stack)> stacks) =>
        stacks.SelectMany(s => s.Stack.Members).FirstOrDefault(m => m.Spec.Config.ContainsKey("dashboard"));

    private static string[] ExportedVarsOf(Shape host)
    {
        var unit = DashboardConfig(host, "unit") ?? "homepage.service";
        var quadlet = Path.Combine(PodmanProvisioner.QuadletSourceDir(host) ?? "", Path.GetFileNameWithoutExtension(unit) + ".container");
        return File.Exists(quadlet) ? HomepageDashboard.ExportedVars(File.ReadAllText(quadlet)).ToArray() : Array.Empty<string>();
    }

    private static string? DashboardConfig(Shape host, string key) =>
        host.Spec.Config["dashboard"] is System.Collections.IDictionary d ? d[key]?.ToString() : null;

    // Push the rendered file and restart the unit, only if the content differs from what is live.
    internal static async Task<(string msg, string? failed)> DeployAsync(Shape host, string yaml, INodeExec exec)
    {
        var rel = DashboardConfig(host, "services") ?? "homepage/services.yaml";
        var unit = DashboardConfig(host, "unit") ?? "homepage.service";
        if (host.Spec.Node is not { Length: > 0 } node || host.Spec.Ctid is not { Length: > 0 } ctid)
            return ("", "dashboard host shape has no node/ctid");
        var user = host.Spec.Config.Str("user") ?? "podman";
        var target = PodmanProvisioner.AssetsTarget(host);
        var path = $"{target}/{rel}";
        var desired = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(yaml))).ToLowerInvariant();

        var live = await exec.InContainerAsync(node, ctid, $"sha256sum {path} 2>/dev/null | cut -d' ' -f1");
        if (live.Ok && live.Stdout.Trim() == desired)
            return ($"dashboard current on CT {ctid} ({path}, sha {desired[..12]}) — no restart", null);

        var pushed = await PodmanProvisioner.PushFilesAsync(exec, node, ctid, user, target,
            new[] { (rel, Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml)), false) });
        if (pushed.Failed is not null) return ("", pushed.Failed);

        // The unit only exists once the Monitoring host has been converged with the homepage
        // quadlet. Before that (the first merge, a rebuilt host) the file is STAGED — the converge
        // renders the assets dir and starts the unit, which then reads it — and this says so
        // loudly rather than failing a run whose actual job (getting the file there) succeeded.
        var loaded = await exec.InContainerAsync(node, ctid, string.Join("\n", new[]
        {
            $"UID_N=$(id -u {user})",
            PodmanProvisioner.UserCmd(user, $"systemctl --user show -p LoadState --value {unit}"),
        }));
        if (!loaded.Ok || loaded.Stdout.Trim() != "loaded")
            return ($"WARNING: dashboard staged on CT {ctid} at {path} (sha {desired[..12]}) but {unit} is not loaded there — converge the Monitoring host (deploy-monitoring, apply=true, only=podman-host) to start it", null);

        // Homepage caches its config; restart the unit so the new file is read. Same runuser +
        // XDG_RUNTIME_DIR incantation the podman deploy script uses for `systemctl --user`.
        var restart = await exec.InContainerAsync(node, ctid, string.Join("\n", new[]
        {
            "set -e",
            $"UID_N=$(id -u {user})",
            PodmanProvisioner.UserCmd(user, $"systemctl --user restart {unit}"),
            PodmanProvisioner.UserCmd(user, $"systemctl --user is-active {unit}"),
        }));
        if (!restart.Ok) return ("", $"restarting {unit} failed: {restart.Stderr}");
        return ($"dashboard updated on CT {ctid}: {path} (sha {desired[..12]}), {unit} restarted", null);
    }
}
