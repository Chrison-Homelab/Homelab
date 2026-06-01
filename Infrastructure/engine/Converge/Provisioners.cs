using System.Text;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Context handed to provisioners at apply time.
public sealed record ConvergeContext(
    INodeExec Exec,
    SecretsEnv Secrets,
    IReadOnlyDictionary<string, Shape> ByName,
    SecretDeriver Deriver);

public enum ApplyOutcome { NoChange, Applied, Skipped, Failed }

public sealed record ApplyResult(ApplyOutcome Outcome, string Message)
{
    public static ApplyResult NoChange(string m) => new(ApplyOutcome.NoChange, m);
    public static ApplyResult Applied(string m) => new(ApplyOutcome.Applied, m);
    public static ApplyResult Skipped(string m) => new(ApplyOutcome.Skipped, m);
    public static ApplyResult Failed(string m) => new(ApplyOutcome.Failed, m);
}

// An app-keyed post-create provisioner. PlanSteps() describes what apply would do;
// ApplyAsync() performs it idempotently (read current state, change only if needed).
public interface IAppProvisioner
{
    string App { get; }
    IEnumerable<string> PlanSteps(Shape shape);
    Task<ApplyResult> ApplyAsync(Shape shape, ConvergeContext ctx);
}

public sealed class ProvisionerRegistry
{
    private readonly Dictionary<string, IAppProvisioner> _byApp;
    private readonly IAppProvisioner _default = new DefaultProvisioner();

    public ProvisionerRegistry(IEnumerable<IAppProvisioner> provisioners) =>
        _byApp = provisioners.ToDictionary(p => p.App, StringComparer.Ordinal);

    public IAppProvisioner For(string? app) =>
        app is not null && _byApp.TryGetValue(app, out var p) ? p : _default;

    // The app slugs with a dedicated (non-default) provisioner. Exposed read-only
    // so the catalogue drift-guard test can assert each one exists in
    // app-catalogue.yaml without reaching into private state. Does not affect
    // dispatch — additive accessor only.
    public IReadOnlyCollection<string> RegisteredApps => _byApp.Keys;

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

    public static string Describe(object? v) =>
        v is IEnumerable<object> e ? string.Join(",", e.Select(x => x?.ToString())) : v?.ToString() ?? "";
}

public sealed class DefaultProvisioner : IAppProvisioner
{
    public string App => "*";
    public IEnumerable<string> PlanSteps(Shape s)
    {
        yield return "create CT + install app via community-scripts; no post-create config declared";
    }
    public Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx) =>
        Task.FromResult(ApplyResult.NoChange("no post-create config"));
}

public sealed class ForgejoProvisioner : IAppProvisioner
{
    public string App => "forgejo";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var root = s.Spec.Config.Str("rootUrl");
        if (root is not null)
            yield return $"set [server] ROOT_URL + DOMAIN → {root} (restart forgejo if changed)";
        yield return "exposes action 'generate-runner-token' for dependents (forgejo-runner)";
    }

    // Idempotent: read current ROOT_URL, only rewrite + restart if it differs.
    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var desired = s.Spec.Config.Str("rootUrl");
        if (desired is null) return ApplyResult.NoChange("no rootUrl configured");
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.Failed("missing node/ctid");

        var read = await ctx.Exec.InContainerAsync(node, ctid, "grep -m1 -E ^ROOT_URL /etc/forgejo/app.ini");
        if (!read.Ok) return ApplyResult.Failed($"could not read app.ini: {read.Stderr}");

        var current = read.Stdout.Contains('=') ? read.Stdout.Split('=', 2)[1].Trim() : "";
        if (Norm(current) == Norm(desired))
            return ApplyResult.NoChange($"ROOT_URL already {current}");

        var host = new Uri(desired).Host;
        var set =
            $"sed -i \"s|^ROOT_URL = .*|ROOT_URL = {desired}|\" /etc/forgejo/app.ini && " +
            $"sed -i \"s|^DOMAIN = .*|DOMAIN = {host}|\" /etc/forgejo/app.ini && " +
            "systemctl restart forgejo";
        var res = await ctx.Exec.InContainerAsync(node, ctid, set);
        return res.Ok
            ? ApplyResult.Applied($"ROOT_URL {current} → {desired} (restarted)")
            : ApplyResult.Failed($"set failed: {res.Stderr}");
    }

    private static string Norm(string url) => url.TrimEnd('/');
}

// The remaining apps were provisioned by hand this session; their ApplyAsync is
// deferred until idempotency checks land (re-registering a live runner or
// re-creating a tunnel would churn the working stack). Plan still describes them.
public sealed class ForgejoRunnerProvisioner : IAppProvisioner
{
    public string App => "forgejo-runner";
    public IEnumerable<string> PlanSteps(Shape s)
    {
        var dep = s.Spec.DependsOn.FirstOrDefault() ?? "forgejo";
        var labels = s.Spec.Config.TryGetValue("runnerLabels", out var l) ? ConfigExt.Describe(l) : "(default)";
        yield return $"resolve runner instance URL from dependency '{dep}'";
        yield return $"register runner (labels: {labels}) using derived token, then start the daemon";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        // Idempotency: if the runner daemon is already active, leave it alone.
        var active = await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active forgejo-runner");
        if (active.Stdout.Trim() == "active") return ApplyResult.NoChange("forgejo-runner already active");

        var depName = s.Spec.DependsOn.FirstOrDefault() ?? "forgejo";
        if (!ctx.ByName.TryGetValue(depName, out var dep) || dep.Spec.Node is not { } dn || dep.Spec.Ctid is not { } dc)
            return ApplyResult.Failed($"dependency '{depName}' not resolvable");
        var ip = await ctx.Exec.InContainerAsync(dn, dc, "hostname -I | awk '{print $1}'");
        if (!ip.Ok || ip.Stdout.Length == 0) return ApplyResult.Failed("could not resolve forgejo address");
        var instance = $"http://{ip.Stdout.Trim()}:3000";

        var sec = s.Spec.Secrets.FirstOrDefault(x => x.ValueFrom.Service is not null);
        if (sec is null) return ApplyResult.Failed("no service-derived runner token declared");
        var token = await ctx.Deriver.ResolveAsync(sec.ValueFrom);
        var labels = s.Spec.Config.TryGetValue("runnerLabels", out var l) ? ConfigExt.Describe(l) : "homelab";

        // Mirrors the manual fix: classic register → default config → restart.
        var cmd =
            $"systemctl stop forgejo-runner; cd /root && forgejo-runner register --no-interactive " +
            $"--instance {instance} --token {token} --name forgejo-runner --labels {labels} && " +
            "forgejo-runner generate-config > /etc/forgejo-runner/config.yaml && systemctl restart forgejo-runner";
        var res = await ctx.Exec.InContainerAsync(node, ctid, cmd);
        return res.Ok ? ApplyResult.Applied($"registered + started ({instance}, labels {labels})")
                      : ApplyResult.Failed($"register failed: {res.Stderr}");
    }
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

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");
        if (s.Spec.Config.Str("githubOrg") is not { } org) return ApplyResult.Failed("no githubOrg in config");
        var name = s.Spec.Config.Str("runnerName") ?? $"homelab-{node}";

        var sec = s.Spec.Secrets.FirstOrDefault(x => x.ValueFrom.Provider?.Name == "github");
        if (sec?.ValueFrom.Provider?.Auth is not { } authSrc) return ApplyResult.Failed("no github provider secret declared");
        var pat = await ctx.Deriver.ResolveAsync(authSrc);
        var gh = new GithubApi(pat);

        // Idempotency: a runner of this name already online → leave it alone.
        if (await gh.IsOrgRunnerOnlineAsync(org, name, CancellationToken.None))
            return ApplyResult.NoChange($"runner '{name}' already online in {org}");

        var token = await ctx.Deriver.ResolveAsync(sec.ValueFrom);   // mint org registration token
        var cmd =
            $"systemctl stop actions-runner 2>/dev/null; cd /opt/actions-runner && " +
            $"runuser -u runner -- ./config.sh --unattended --replace --url https://github.com/{org} " +
            $"--token {token} --name {name} --labels homelab --runnergroup Default && systemctl start actions-runner";
        var res = await ctx.Exec.InContainerAsync(node, ctid, cmd);
        return res.Ok ? ApplyResult.Applied($"registered '{name}' to {org}")
                      : ApplyResult.Failed($"config.sh failed: {res.Stderr}");
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

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");
        if (s.Spec.Config.Str("tunnel") is not { } tunnelName) return ApplyResult.Failed("no tunnel in config");
        var ingress = ParseIngress(s.Spec.Config);
        if (ingress.Count == 0) return ApplyResult.Failed("no ingress in config");

        var sec = s.Spec.Secrets.FirstOrDefault(x => x.ValueFrom.Provider?.Name == "cloudflare");
        if (sec?.ValueFrom.Provider?.Auth is not { } authSrc) return ApplyResult.Failed("no cloudflare provider secret declared");
        var api = new CloudflareApi(await ctx.Deriver.ResolveAsync(authSrc));
        var ct = CancellationToken.None;

        var zoneName = string.Join('.', ingress[0].host.Split('.')[^2..]);
        var zone = await api.GetZoneAsync(zoneName, ct);
        var tunnelId = await api.FindTunnelIdAsync(zone.AccountId, tunnelName, ct);
        var svcActive = (await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active cloudflared")).Stdout.Trim() == "active";

        var dnsPresent = true;
        foreach (var (host, _) in ingress)
            if (!await api.DnsExistsAsync(zone.ZoneId, host, ct)) { dnsPresent = false; break; }

        // Idempotency (ADD-ONLY): tunnel + all DNS + active connector → done.
        if (tunnelId is not null && dnsPresent && svcActive)
            return ApplyResult.NoChange($"tunnel '{tunnelName}' + DNS present, cloudflared active");

        tunnelId ??= await api.CreateTunnelAsync(zone.AccountId, tunnelName, ct);
        await api.SetTunnelConfigAsync(zone.AccountId, tunnelId, BuildIngressJson(ingress), ct);
        var token = await api.GetTunnelTokenAsync(zone.AccountId, tunnelId, ct);

        var install = $"cloudflared service uninstall 2>/dev/null; cloudflared service install {token}";
        var res = await ctx.Exec.InContainerAsync(node, ctid, install);
        if (!res.Ok) return ApplyResult.Failed($"cloudflared install failed: {res.Stderr}");

        foreach (var (host, _) in ingress)
            if (!await api.DnsExistsAsync(zone.ZoneId, host, ct))
                await api.CreateCnameAsync(zone.ZoneId, host, $"{tunnelId}.cfargotunnel.com", ct);

        return ApplyResult.Applied($"tunnel '{tunnelName}' ensured + token installed ({ingress.Count} ingress)");
    }

    private static List<(string host, string service)> ParseIngress(Dictionary<string, object?> c)
    {
        var list = new List<(string, string)>();
        if (c.TryGetValue("ingress", out var v) && v is IEnumerable<object> items)
            foreach (var it in items)
                if (it is System.Collections.IDictionary d)
                    list.Add((d["hostname"]?.ToString() ?? "", d["service"]?.ToString() ?? ""));
        return list;
    }

    // NOTE: service is taken from config as-is (e.g. http://forgejo:3000). A live
    // create should resolve the logical host to the CT IP first; this branch only
    // runs when the tunnel is absent (never on the already-provisioned stack).
    private static string BuildIngressJson(List<(string host, string service)> ingress)
    {
        var sb = new StringBuilder("[");
        foreach (var (host, service) in ingress)
            sb.Append($"{{\"hostname\":\"{host}\",\"service\":\"{service}\"}},");
        sb.Append("{\"service\":\"http_status:404\"}]");
        return sb.ToString();
    }
}
