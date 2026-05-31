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
        var creator = new CommunityScriptsCreator(exec);
        var ct = CancellationToken.None;

        var stackName = loaded.Stack?.Metadata.Name ?? Path.GetFileName(_stackDir);
        Console.WriteLine($"Converge APPLY — stack '{stackName}'  ({ordered.Count} member(s), dependency order)\n");

        int failed = 0, applied = 0, nochange = 0, skipped = 0;
        foreach (var s in ordered)
        {
            var sp = s.Spec;
            Console.WriteLine($"▸ {s.Metadata.Name}  (ctid {sp.Ctid}, app '{sp.App}', node {sp.Node})");

            // Lifecycle: ensure the CT exists (create via community-scripts if absent).
            if (sp.Node is { } node && sp.Ctid is { } ctid)
            {
                if (await creator.ExistsAsync(node, ctid, ct))
                {
                    Console.WriteLine($"    CT {ctid} exists");
                }
                else
                {
                    IReadOnlyDictionary<string, string>? extra = null;
                    if (sp.App == "forgejo-runner")
                    {
                        try { extra = await ForgejoRunnerCreateVarsAsync(s, ctx, ct); }
                        catch (Exception ex) { Console.WriteLine($"    FAILED: create vars — {ex.Message}"); failed++; Console.WriteLine(); continue; }
                    }
                    var created = await creator.EnsureAsync(s, extra, ct);
                    Console.WriteLine($"    CREATE {created.Outcome.ToString().ToUpperInvariant()}: {created.Message}");
                    if (created.Outcome == ApplyOutcome.Failed) { failed++; Console.WriteLine(); continue; }
                    if (created.Outcome == ApplyOutcome.Applied) applied++;
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

    // The forgejo-runner DEV script registers AT create time, so it needs the
    // instance URL + a derived runner token (+ uuid + labels) as create vars.
    // Only computed when the CT is absent (avoids minting an unused token).
    private static async Task<IReadOnlyDictionary<string, string>> ForgejoRunnerCreateVarsAsync(
        Shapes.Shape s, ConvergeContext ctx, CancellationToken ct)
    {
        var depName = s.Spec.DependsOn.FirstOrDefault() ?? "forgejo";
        if (!ctx.ByName.TryGetValue(depName, out var dep) || dep.Spec.Node is not { } dn || dep.Spec.Ctid is not { } dc)
            throw new InvalidOperationException($"dependency '{depName}' not resolvable");
        var ip = await ctx.Exec.InContainerAsync(dn, dc, "hostname -I | awk '{print $1}'", ct);
        if (!ip.Ok || ip.Stdout.Length == 0) throw new InvalidOperationException("could not resolve forgejo address");

        var sec = s.Spec.Secrets.FirstOrDefault(x => x.ValueFrom.Service is not null)
            ?? throw new InvalidOperationException("no service-derived runner token declared");
        var token = await ctx.Deriver.ResolveAsync(sec.ValueFrom, ct);
        var labels = s.Spec.Config.TryGetValue("runnerLabels", out var l) ? ConfigExt.Describe(l) : "homelab";

        return new Dictionary<string, string>
        {
            ["var_forgejo_instance"] = $"http://{ip.Stdout.Trim()}:3000",
            ["var_forgejo_runner_token"] = token,
            ["var_forgejo_runner_uuid"] = Guid.NewGuid().ToString(),
            ["var_runner_labels"] = labels,
        };
    }
}
