using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// An app-keyed post-create provisioner. PlanSteps() describes what apply WOULD
// do (derived from the shape's config/secrets/dependsOn). Apply() is the next
// BL-010 increment — it will encode the steps we ran by hand for the DevOps stack.
public interface IAppProvisioner
{
    string App { get; }
    IEnumerable<string> PlanSteps(Shape shape);
    // Task ApplyAsync(Shape shape, ConvergeContext ctx)  -> TODO (live SSH/pct/API).
}

public sealed class ProvisionerRegistry
{
    private readonly Dictionary<string, IAppProvisioner> _byApp;
    private readonly IAppProvisioner _default = new DefaultProvisioner();

    public ProvisionerRegistry(IEnumerable<IAppProvisioner> provisioners) =>
        _byApp = provisioners.ToDictionary(p => p.App, StringComparer.Ordinal);

    public IAppProvisioner For(string? app) =>
        app is not null && _byApp.TryGetValue(app, out var p) ? p : _default;

    public static ProvisionerRegistry Default() => new(new IAppProvisioner[]
    {
        new ForgejoProvisioner(),
        new ForgejoRunnerProvisioner(),
        new GithubRunnerProvisioner(),
        new CloudflaredProvisioner(),
    });
}

internal static class ConfigExt
{
    public static string? Str(this Dictionary<string, object?> c, string key) =>
        c.TryGetValue(key, out var v) ? v?.ToString() : null;
}

public sealed class DefaultProvisioner : IAppProvisioner
{
    public string App => "*";
    public IEnumerable<string> PlanSteps(Shape s)
    {
        yield return "create CT + install app via community-scripts; no post-create config declared";
    }
}

public sealed class ForgejoProvisioner : IAppProvisioner
{
    public string App => "forgejo";
    public IEnumerable<string> PlanSteps(Shape s)
    {
        var root = s.Spec.Config.Str("rootUrl");
        if (root is not null)
            yield return $"set [server] ROOT_URL + DOMAIN → {root} (restart forgejo)";
        yield return "exposes action 'generate-runner-token' for dependents (forgejo-runner)";
    }
}

public sealed class ForgejoRunnerProvisioner : IAppProvisioner
{
    public string App => "forgejo-runner";
    public IEnumerable<string> PlanSteps(Shape s)
    {
        var dep = s.Spec.DependsOn.FirstOrDefault() ?? "forgejo";
        var labels = s.Spec.Config.TryGetValue("runnerLabels", out var l) ? Describe(l) : "(default)";
        yield return $"resolve runner instance URL from dependency '{dep}'";
        yield return $"register runner (labels: {labels}) using derived token, then start the daemon";
    }

    private static string Describe(object? v) =>
        v is IEnumerable<object> e ? string.Join(",", e.Select(x => x?.ToString())) : v?.ToString() ?? "";
}

public sealed class GithubRunnerProvisioner : IAppProvisioner
{
    public string App => "github-runner";
    public IEnumerable<string> PlanSteps(Shape s)
    {
        var org = s.Spec.Config.Str("githubOrg") ?? "(org)";
        yield return $"mint org registration token (provider github, org {org})";
        yield return $"run config.sh against https://github.com/{org}, start actions-runner";
    }
}

public sealed class CloudflaredProvisioner : IAppProvisioner
{
    public string App => "cloudflared";
    public IEnumerable<string> PlanSteps(Shape s)
    {
        var tunnel = s.Spec.Config.Str("tunnel") ?? "(tunnel)";
        var ingressCount = s.Spec.Config.TryGetValue("ingress", out var ig) && ig is IEnumerable<object> e ? e.Count() : 0;
        yield return $"ensure tunnel '{tunnel}' + DNS exist (ADD-ONLY — never touch existing; CLAUDE.md)";
        yield return $"apply {ingressCount} ingress rule(s); install tunnel token; start cloudflared";
    }
}
