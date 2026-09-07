using Homelab.Infrastructure.Dashboard;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// `homelab-infra security-updates <stacks-dir> [--apply]` — run the SecurityUpdatesReconciler
// over every LXC member of every stack, without converging anything else. Converge applies
// the same baseline per member; this exists for the one-time fleet rollout and for re-checking
// it, where a full converge of five stacks would be the wrong tool (it also re-runs every app
// provisioner). Without --apply it only lists what would run.
public static class SecurityUpdatesCommand
{
    public static async Task<int> RunAsync(string[] args, INodeExec exec, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2) { stderr.WriteLine("usage: homelab-infra security-updates <stacks-dir> [--apply]"); return 2; }
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root)) { stderr.WriteLine($"directory not found: {root}"); return 2; }
        var apply = args.Skip(2).Contains("--apply");

        var stacks = DashboardCommand.LoadAllStacks(root, stderr);
        var rec = new SecurityUpdatesReconciler(exec);
        int applied = 0, unchanged = 0, skipped = 0, failed = 0;
        foreach (var (stackName, stack) in stacks)
            foreach (var m in stack.Members)
            {
                if (m.Spec.Ctid is null || m.Spec.Node is null) continue;
                if (m.Metadata.Tags.Contains("retired", StringComparer.OrdinalIgnoreCase)) { stdout.WriteLine($"  {stackName}/{m.Metadata.Name} (CT {m.Spec.Ctid}): retired — skipped"); skipped++; continue; }
                if (!apply) { stdout.WriteLine($"  {stackName}/{m.Metadata.Name} (CT {m.Spec.Ctid} on {m.Spec.Node}): {SecurityUpdatesReconciler.PlanStep(m)}"); continue; }
                var r = await rec.ReconcileAsync(m);
                stdout.WriteLine($"  {stackName}/{m.Metadata.Name} (CT {m.Spec.Ctid}): {r.Outcome.ToString().ToUpperInvariant()} — {r.Message}");
                switch (r.Outcome)
                {
                    case ApplyOutcome.Applied: applied++; break;
                    case ApplyOutcome.Failed: failed++; break;
                    case ApplyOutcome.Skipped: skipped++; break;
                    default: unchanged++; break;
                }
            }
        if (apply) stdout.WriteLine($"security-updates: {applied} applied, {unchanged} already in place, {skipped} skipped, {failed} failed");
        else stdout.WriteLine("(plan only — re-run with --apply)");
        return failed > 0 ? 1 : 0;
    }
}
