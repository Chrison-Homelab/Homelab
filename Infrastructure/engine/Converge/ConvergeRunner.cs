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
            ? "Plan OK — all declared secrets resolvable. (Apply not yet implemented; this is a dry run.)"
            : $"Plan has {blocked} unresolved secret input(s) — fix secrets.env before apply.");
        return blocked == 0 ? 0 : 1;
    }
}
