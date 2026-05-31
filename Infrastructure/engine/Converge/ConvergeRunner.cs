using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Orchestrates a converge PLAN: load a stack, order by dependsOn, and for each
// member report the post-create work (config, secret resolution, provisioner
// steps). Apply (live mutation) is the next BL-010 increment.
public sealed class ConvergeRunner
{
    private readonly string _stackDir;
    private readonly SecretsEnv _env;

    public ConvergeRunner(string stackDir, SecretsEnv env)
    {
        _stackDir = stackDir;
        _env = env;
    }

    public int Plan()
    {
        var loaded = ShapeLoader.LoadStack(_stackDir);
        var ordered = TopologicalSorter.Order(loaded.Members);
        var resolver = new SecretResolver(_env);
        var registry = ProvisionerRegistry.Default();

        var stackName = loaded.Stack?.Metadata.Name ?? Path.GetFileName(_stackDir);
        Console.WriteLine($"Converge plan — stack '{stackName}'  ({ordered.Count} member(s), dependency order)\n");

        var blocked = 0;
        foreach (var s in ordered)
        {
            var sp = s.Spec;
            var ch = sp.Source?.Channel ?? "stable";
            Console.WriteLine($"▸ {s.Metadata.Name}  (ctid {sp.Ctid}, app '{sp.App}', node {sp.Node}, channel {ch})");
            if (sp.DependsOn.Count > 0)
                Console.WriteLine($"    dependsOn: {string.Join(", ", sp.DependsOn)}");

            if (sp.Config.Count > 0)
                Console.WriteLine($"    config:    {string.Join(", ", sp.Config.Keys)}");

            var secrets = resolver.Plan(sp);
            foreach (var r in secrets)
            {
                var mark = r.Ready ? "✓" : "✗";
                if (!r.Ready) blocked++;
                Console.WriteLine($"    secret {mark} {r.Name}: {r.Description}");
            }

            foreach (var step in registry.For(sp.App).PlanSteps(s))
                Console.WriteLine($"    post-create: {step}");

            Console.WriteLine();
        }

        Console.WriteLine(blocked == 0
            ? "Plan OK — all declared secrets resolvable. (Run with --apply to converge.)"
            : $"Plan has {blocked} unresolved secret input(s) — fix secrets.env before apply.");
        return blocked == 0 ? 0 : 1;
    }

    // Live converge — idempotent per provisioner. Guards: the CT must already
    // exist (create stays with the renderer for now); required env secrets must
    // be present. Provisioners that aren't idempotent-safe yet report Skipped.
    public async Task<int> ApplyAsync()
    {
        var loaded = ShapeLoader.LoadStack(_stackDir);
        var ordered = TopologicalSorter.Order(loaded.Members);
        var resolver = new SecretResolver(_env);
        var registry = ProvisionerRegistry.Default();
        var exec = new NodeExec();
        var byName = ordered.ToDictionary(s => s.Metadata.Name, StringComparer.Ordinal);
        var deriver = new SecretDeriver(_env, exec, byName);
        var ctx = new ConvergeContext(exec, _env, byName, deriver);

        var stackName = loaded.Stack?.Metadata.Name ?? Path.GetFileName(_stackDir);
        Console.WriteLine($"Converge APPLY — stack '{stackName}'  ({ordered.Count} member(s), dependency order)\n");

        int failed = 0, applied = 0, nochange = 0, skipped = 0;
        foreach (var s in ordered)
        {
            var sp = s.Spec;
            Console.WriteLine($"▸ {s.Metadata.Name}  (ctid {sp.Ctid}, app '{sp.App}', node {sp.Node})");

            // Guard: CT must exist (we don't create here).
            if (sp.Node is { } node && sp.Ctid is { } ctid)
            {
                var status = await exec.OnNodeAsync(node, $"pct status {ctid}");
                if (!status.Ok)
                {
                    Console.WriteLine($"    FAILED: CT {ctid} not found on {node} (create via the renderer first)");
                    failed++; Console.WriteLine(); continue;
                }
            }

            // Guard: required env secrets must be present.
            var missing = resolver.Plan(sp).Where(r => !r.Ready).ToList();
            if (missing.Count > 0)
            {
                foreach (var m in missing) Console.WriteLine($"    FAILED: secret '{m.Name}' — {m.Description}");
                failed++; Console.WriteLine(); continue;
            }

            try
            {
                var result = await registry.For(sp.App).ApplyAsync(s, ctx);
                Console.WriteLine($"    {result.Outcome.ToString().ToUpperInvariant()}: {result.Message}");
                switch (result.Outcome)
                {
                    case ApplyOutcome.Applied: applied++; break;
                    case ApplyOutcome.NoChange: nochange++; break;
                    case ApplyOutcome.Skipped: skipped++; break;
                    default: failed++; break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    FAILED: {ex.Message}");
                failed++;
            }
            Console.WriteLine();
        }

        Console.WriteLine($"Apply summary — {applied} applied, {nochange} no-change, {skipped} skipped, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
