using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        new PangolinProvisioner(),
        new QbittorrentProvisioner(),
        new ProwlarrProvisioner(),
        new SonarrProvisioner(),
        new RadarrProvisioner(),
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
        if (ingressCount == 0)
        {
            yield return $"join existing tunnel '{tunnel}' as a replica connector (ingress + DNS owned by the primary)";
            yield return "install the shared tunnel token; start cloudflared";
        }
        else
        {
            yield return $"ensure tunnel '{tunnel}' + DNS exist (ADD-ONLY — never touch existing; CLAUDE.md)";
            yield return $"apply {ingressCount} ingress rule(s); install tunnel token; start cloudflared";
            var allow = ParseAccessAllow(s.Spec.Config);
            if (allow.Count > 0)
                yield return $"ensure a CF Access OTP app per hostname (allow {string.Join(", ", allow)}) — ADD-ONLY";
        }
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");
        if (s.Spec.Config.Str("tunnel") is not { } tunnelName) return ApplyResult.Failed("no tunnel in config");
        var sec = s.Spec.Secrets.FirstOrDefault(x => x.ValueFrom.Provider?.Name == "cloudflare");
        if (sec?.ValueFrom.Provider?.Auth is not { } authSrc) return ApplyResult.Failed("no cloudflare provider secret declared");
        var api = new CloudflareApi(await ctx.Deriver.ResolveAsync(authSrc));
        var ct = CancellationToken.None;
        var ingress = ParseIngress(s.Spec.Config);

        // Replica connector (no ingress): join an EXISTING tunnel owned by the
        // primary — install the shared token + start cloudflared, nothing else.
        // Cloudflare load-balances multiple connectors on one tunnel; ingress + DNS
        // are tunnel-level and owned by the primary (the shape's replicaOf).
        if (ingress.Count == 0)
        {
            var acct = Environment.GetEnvironmentVariable("CF_ACCOUNT_ID");
            if (string.IsNullOrWhiteSpace(acct))
                return ApplyResult.Failed("replica needs CF_ACCOUNT_ID (env) to locate the shared tunnel");
            var rid = await api.FindTunnelIdAsync(acct, tunnelName, ct);
            if (rid is null) return ApplyResult.Failed($"tunnel '{tunnelName}' not found — deploy the primary connector first");
            if ((await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active cloudflared")).Stdout.Trim() == "active")
                return ApplyResult.NoChange($"replica connector already active on tunnel '{tunnelName}'");
            var rtoken = await api.GetTunnelTokenAsync(acct, rid, ct);
            var rres = await ctx.Exec.InContainerAsync(node, ctid,
                $"cloudflared service uninstall 2>/dev/null; cloudflared service install {rtoken}");
            if (!rres.Ok) return ApplyResult.Failed($"cloudflared install failed: {rres.Stderr}");
            return ApplyResult.Applied($"replica connector joined tunnel '{tunnelName}' (token installed)");
        }

        var zoneName = string.Join('.', ingress[0].host.Split('.')[^2..]);
        var zone = await api.GetZoneAsync(zoneName, ct);
        var tunnelId = await api.FindTunnelIdAsync(zone.AccountId, tunnelName, ct);
        var svcActive = (await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active cloudflared")).Stdout.Trim() == "active";

        var dnsPresent = true;
        foreach (var (host, _, _) in ingress)
            if (!await api.DnsExistsAsync(zone.ZoneId, host, ct)) { dnsPresent = false; break; }

        // Content-aware ingress drift (#165): the live tunnel ingress must match the
        // shape's (hostname → service), else re-push. Without this, adding/changing a
        // hostname whose DNS already exists is silently never pushed.
        var ingressDrift = false;
        if (tunnelId is not null)
        {
            var live = await api.GetTunnelIngressAsync(zone.AccountId, tunnelId, ct);
            var desired = ingress.Select(i => (i.host, i.service)).ToHashSet();
            ingressDrift = !desired.SetEquals(live.ToHashSet());
        }

        // CF Access gating (ADD-ONLY): each ingress hostname gets a self-hosted Access
        // app + an allow-by-email policy so the admin UI is never exposed raw — login is
        // One-Time PIN (no other IdP). Driven by config.access.allow; empty → no gating.
        // App is keyed by domain + policy by name, so this is find-or-create (re-run safe).
        var allowEmails = ParseAccessAllow(s.Spec.Config);
        var gated = 0;
        foreach (var (host, _, _) in ingress)
        {
            if (allowEmails.Count == 0) break;
            var appId = await api.FindAccessAppIdAsync(zone.AccountId, host, ct);
            if (appId is null)
            {
                appId = await api.CreateAccessAppAsync(zone.AccountId, $"{host.Split('.')[0]} (Media)", host, "24h", ct);
                gated++;
            }
            if (!await api.AccessPolicyExistsAsync(zone.AccountId, appId, AccessPolicyName, ct))
            {
                await api.CreateAccessAllowEmailPolicyAsync(zone.AccountId, appId, AccessPolicyName, allowEmails, ct);
                gated++;
            }
        }
        var gateNote = allowEmails.Count > 0 ? $", {ingress.Count} host(s) Access-gated" : "";

        // Idempotency (ADD-ONLY): tunnel + all DNS + active connector + gating + ingress in place → done.
        if (tunnelId is not null && dnsPresent && svcActive && gated == 0 && !ingressDrift)
            return ApplyResult.NoChange($"tunnel '{tunnelName}' + DNS present, cloudflared active{gateNote}");

        // (Re)provision the connector only when the tunnel is absent or not running —
        // never disturb an already-active connector just to add a gate.
        if (tunnelId is null || !svcActive)
        {
            tunnelId ??= await api.CreateTunnelAsync(zone.AccountId, tunnelName, ct);
            await api.SetTunnelConfigAsync(zone.AccountId, tunnelId, BuildIngressJson(ingress), ct);
            var token = await api.GetTunnelTokenAsync(zone.AccountId, tunnelId, ct);
            var install = $"cloudflared service uninstall 2>/dev/null; cloudflared service install {token}";
            var res = await ctx.Exec.InContainerAsync(node, ctid, install);
            if (!res.Ok) return ApplyResult.Failed($"cloudflared install failed: {res.Stderr}");
        }
        else if (ingressDrift)
        {
            // Connector is already up — just push the corrected ingress, no reinstall (#165).
            await api.SetTunnelConfigAsync(zone.AccountId, tunnelId, BuildIngressJson(ingress), ct);
        }

        foreach (var (host, _, _) in ingress)
            if (!await api.DnsExistsAsync(zone.ZoneId, host, ct))
                await api.CreateCnameAsync(zone.ZoneId, host, $"{tunnelId}.cfargotunnel.com", ct);

        var changes = (gated > 0 ? $"; {gated} Access change(s)" : "") + (ingressDrift ? "; ingress re-pushed" : "");
        return ApplyResult.Applied($"tunnel '{tunnelName}' ensured ({ingress.Count} ingress){changes}");
    }

    private const string AccessPolicyName = "allow-homelab-admins";

    // config.access.allow — emails permitted through the CF Access OTP gate.
    private static List<string> ParseAccessAllow(Dictionary<string, object?> c)
    {
        var emails = new List<string>();
        if (c.TryGetValue("access", out var a) && a is System.Collections.IDictionary ad
            && ad["allow"] is IEnumerable<object> items)
            foreach (var it in items)
                if (it?.ToString() is { Length: > 0 } e) emails.Add(e);
        return emails;
    }

    // Captures originRequest too (e.g. { noTLSVerify: true }) so a re-push (#165)
    // faithfully preserves it — otherwise re-pushing would strip noTLSVerify from
    // the self-signed https origins (pdm/proxmox) and break them.
    private static List<(string host, string service, string? origin)> ParseIngress(Dictionary<string, object?> c)
    {
        var list = new List<(string, string, string?)>();
        if (c.TryGetValue("ingress", out var v) && v is IEnumerable<object> items)
            foreach (var it in items)
                if (it is System.Collections.IDictionary d)
                {
                    string? origin = null;
                    if (d.Contains("originRequest") && d["originRequest"] is System.Collections.IDictionary od)
                    {
                        var map = new Dictionary<string, object?>();
                        foreach (System.Collections.DictionaryEntry e in od) map[e.Key?.ToString() ?? ""] = e.Value;
                        origin = JsonSerializer.Serialize(map);
                    }
                    list.Add((d["hostname"]?.ToString() ?? "", d["service"]?.ToString() ?? "", origin));
                }
        return list;
    }

    // NOTE: service is taken from config as-is (e.g. http://forgejo:3000). A live
    // create should resolve the logical host to the CT IP first.
    private static string BuildIngressJson(List<(string host, string service, string? origin)> ingress)
    {
        var sb = new StringBuilder("[");
        foreach (var (host, service, origin) in ingress)
        {
            sb.Append($"{{\"hostname\":\"{host}\",\"service\":\"{service}\"");
            if (origin is not null) sb.Append($",\"originRequest\":{origin}");
            sb.Append("},");
        }
        sb.Append("{\"service\":\"http_status:404\"}]");
        return sb.ToString();
    }
}

// Pangolin — SSO remote-access reverse proxy (ADR-0007). Runs on-prem behind the
// `core` Cloudflare tunnel, so the stock community-scripts install (which makes
// Traefik own public ingress via Let's Encrypt) is reshaped for "behind cloudflared":
// every Traefik router moves onto the plain-HTTP `web` (:80) entrypoint, dropping TLS
// + the http→https redirect — cloudflared provides the public TLS. Seeds
// /opt/pangolin/config/config.yml, GENERATING + PRESERVING server.secret on the CT
// (so no manual secrets.env step), and for the cloudflared edge rewrites
// traefik/dynamic_config.yml (Traefik's file provider hot-reloads it). Idempotent via
// a managed marker stamped into config.yml. The one-time admin/org setup stays manual.
public sealed class PangolinProvisioner : IAppProvisioner
{
    public string App => "pangolin";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var url = s.Spec.Config.Str("dashboardUrl") ?? "(dashboardUrl)";
        var edge = s.Spec.Config.Str("edge") ?? "cloudflared";
        yield return $"seed /opt/pangolin/config/config.yml (dashboard_url {url}; generate+preserve server.secret; flags)";
        yield return edge == "cloudflared"
            ? "rewrite traefik/dynamic_config.yml for behind-cloudflared (HTTP :80, no Let's Encrypt, no https-redirect)"
            : $"edge '{edge}': leave stock Traefik (Let's Encrypt public ingress)";
        yield return "restart pangolin + gerbil if config changed (idempotent via managed marker)";
    }

    // Stable marker over the managed inputs — when unchanged, apply is a no-op.
    // Exposed so the drift-guard test can compute the expected marker.
    public static string DesiredMarker(Shape s)
    {
        var c = s.Spec.Config;
        var key = string.Join('|',
            c.Str("dashboardUrl") ?? "",
            BaseDomain(s),
            c.Str("edge") ?? "cloudflared",
            Flag(c, "allowRawResources", true),
            Flag(c, "disableSignupWithoutInvite", true),
            Flag(c, "disableUserCreateOrg", false),
            Flag(c, "enableIntegrationApi", false));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");
        if (s.Spec.Config.Str("dashboardUrl") is not { } dashboardUrl) return ApplyResult.Failed("no dashboardUrl in config");
        var host = new Uri(dashboardUrl).Host;
        var edge = s.Spec.Config.Str("edge") ?? "cloudflared";
        var marker = DesiredMarker(s);

        // Config: re-seed only when the managed marker drifted.
        var cur = await ctx.Exec.InContainerAsync(node, ctid,
            "grep -m1 '^# homelab-managed:' /opt/pangolin/config/config.yml 2>/dev/null || true");
        var curMarker = cur.Stdout.Contains(':') ? cur.Stdout.Split(':', 2)[1].Trim() : "";
        string? configMsg = null;
        if (curMarker != marker)
        {
            var ipRes = await ctx.Exec.InContainerAsync(node, ctid, "hostname -I | awk '{print $1}'");
            if (!ipRes.Ok || ipRes.Stdout.Trim().Length == 0) return ApplyResult.Failed("could not resolve CT IP");
            var cmd = BuildWrite(s, marker, host, dashboardUrl, BaseDomain(s), edge, ipRes.Stdout.Trim());
            var res = await ctx.Exec.InContainerAsync(node, ctid, cmd);
            if (!res.Ok) return ApplyResult.Failed($"write/restart failed: {res.Stderr}");
            configMsg = $"seeded config.yml + {(edge == "cloudflared" ? "behind-cloudflared Traefik" : "stock Traefik")}; restarted (marker {marker})";
        }

        // Resources (declarative, #136): reconcile declared admin-UI resources via the
        // integration API (add-only, idempotent by fullDomain). Skipped — not failed —
        // until the org PANGOLIN_API_KEY exists, since that's a post-setup bootstrap secret.
        var (resMsg, resChanged, resFailed) = await ReconcileResourcesAsync(s, ctx, node, ctid);
        if (resFailed is not null) return ApplyResult.Failed(resFailed);

        if (configMsg is null && !resChanged)
            return ApplyResult.NoChange($"config current (marker {marker})" + (resMsg is null ? "" : $"; {resMsg}"));
        return ApplyResult.Applied(string.Join("; ", new[] { configMsg, resMsg }.Where(x => x is not null)));
    }

    // Reconcile declared Pangolin resources (admin UIs) via the integration API on the
    // CT (:3003, Bearer org key). Add-only: find-or-create by fullDomain, set the target
    // (via a local-type site) and ssl=false (behind-cloudflared). Returns (msg, changed,
    // failedReason). Each resource's public hostname is wired separately in the cloudflared
    // ingress (#165 makes that push idempotently).
    private static async Task<(string? msg, bool changed, string? failed)> ReconcileResourcesAsync(
        Shape s, ConvergeContext ctx, string node, string ctid)
    {
        var c = s.Spec.Config;
        if (!(c.TryGetValue("resources", out var rv) && rv is IEnumerable<object> items) || !items.Any())
            return (null, false, null);
        if (ctx.Secrets.Get("PANGOLIN_API_KEY") is not { Length: > 0 } key)
            return ("resources declared but PANGOLIN_API_KEY unset — skipped (create an org API key, then re-run)", false, null);
        if (c.Str("org") is not { Length: > 0 } org)
            return (null, false, "resources declared but config.org (Pangolin org id) is missing");

        var baseDomain = BaseDomain(s);
        var ct = CancellationToken.None;
        var pg = new PangolinClient(ctx.Exec, node, ctid, key);

        // domainId for our baseDomain
        var (dok, droot) = await pg.CallAsync("GET", $"/org/{org}/domains", null, ct);
        if (!dok) return (null, false, "pangolin: integration API unreachable or key invalid (GET domains failed)");
        string? domainId = null;
        foreach (var d in DataArray(droot, "domains"))
            if (d.TryGetProperty("baseDomain", out var bd) && bd.GetString() == baseDomain && d.TryGetProperty("domainId", out var di))
                domainId = di.GetString();
        if (domainId is null) return (null, false, $"pangolin: domain '{baseDomain}' not found in org '{org}'");

        // local site (create if missing — targets the Pangolin host's own services, no Newt)
        int? siteId = null;
        var (sok, sroot) = await pg.CallAsync("GET", $"/org/{org}/sites", null, ct);
        if (sok)
            foreach (var st in DataArray(sroot, "sites"))
                if (st.TryGetProperty("type", out var t) && t.GetString() == "local" && st.TryGetProperty("siteId", out var si))
                    siteId = si.GetInt32();
        if (siteId is null)
        {
            var (cok, croot) = await pg.CallAsync("PUT", $"/org/{org}/site", "{\"name\":\"local\",\"type\":\"local\"}", ct);
            if (!cok || !Data(croot).TryGetProperty("siteId", out var nsi)) return (null, false, "pangolin: failed to create local site");
            siteId = nsi.GetInt32();
        }

        // existing resources, by fullDomain (add-only idempotency)
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (eok, eroot) = await pg.CallAsync("GET", $"/org/{org}/resources", null, ct);
        if (eok)
            foreach (var r in DataArray(eroot, "resources"))
                if (r.TryGetProperty("fullDomain", out var fd) && fd.GetString() is { } f) existing.Add(f);

        int total = 0, created = 0;
        foreach (var it in items)
        {
            if (it is not System.Collections.IDictionary rd) continue;
            total++;
            var sub = rd["subdomain"]?.ToString() ?? "";
            if (existing.Contains($"{sub}.{baseDomain}")) continue;
            var name = rd["name"]?.ToString() ?? sub;
            var tgt = rd["target"] as System.Collections.IDictionary;
            var tip = tgt?["ip"]?.ToString() ?? "localhost";
            var tmethod = tgt?["method"]?.ToString() ?? "http";
            var tport = int.TryParse(tgt?["port"]?.ToString(), out var pp) ? pp : 80;

            var (rok, rroot) = await pg.CallAsync("PUT", $"/org/{org}/resource",
                JsonSerializer.Serialize(new { name, subdomain = sub, http = true, protocol = "tcp", domainId }), ct);
            if (!rok || !Data(rroot).TryGetProperty("resourceId", out var rid))
                return (null, false, $"pangolin: failed to create resource {sub}.{baseDomain}");
            var resourceId = rid.GetInt32();
            await pg.CallAsync("PUT", $"/resource/{resourceId}/target",
                JsonSerializer.Serialize(new { siteId, ip = tip, method = tmethod, port = tport, enabled = true }), ct);
            await pg.CallAsync("POST", $"/resource/{resourceId}", "{\"ssl\":false}", ct); // behind-cloudflared
            created++;
        }
        return ($"{total} resource(s) declared, {created} created", created > 0, null);
    }

    // response.data is sometimes an array, sometimes { <key>: array } — normalise both.
    private static IEnumerable<JsonElement> DataArray(JsonElement root, string key)
    {
        var d = Data(root);
        if (d.ValueKind == JsonValueKind.Array) return d.EnumerateArray().ToList();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            return arr.EnumerateArray().ToList();
        return Array.Empty<JsonElement>();
    }

    private static JsonElement Data(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;

    // Thin client for the Pangolin integration API (:3003), invoked via the node-exec
    // seam (curl inside the CT) — the API isn't publicly reachable (CF Access gates the
    // dashboard hostname) and may not be routable from the engine host.
    private sealed class PangolinClient
    {
        private readonly INodeExec _exec;
        private readonly string _node, _ctid, _key;
        public PangolinClient(INodeExec exec, string node, string ctid, string key)
            => (_exec, _node, _ctid, _key) = (exec, node, ctid, key);

        public async Task<(bool ok, JsonElement root)> CallAsync(string method, string path, string? body, CancellationToken ct)
        {
            var cmd = new StringBuilder($"curl -s -X {method} -H 'Authorization: Bearer {_key}'");
            if (body is not null)
                cmd.Append($" -H 'Content-Type: application/json' -d '{body.Replace("'", "'\\''")}'");
            cmd.Append($" http://localhost:3003/v1{path}");
            var r = await _exec.InContainerAsync(_node, _ctid, cmd.ToString(), ct);
            if (!r.Ok || string.IsNullOrWhiteSpace(r.Stdout)) return (false, default);
            try
            {
                using var doc = JsonDocument.Parse(r.Stdout);
                var root = doc.RootElement.Clone();
                var ok = !root.TryGetProperty("success", out var sc) || sc.ValueKind != JsonValueKind.False;
                return (ok, root);
            }
            catch { return (false, default); }
        }
    }

    private static string BaseDomain(Shape s)
    {
        if (s.Spec.Config.Str("baseDomain") is { Length: > 0 } b) return b;
        var host = s.Spec.Config.Str("dashboardUrl") is { } u ? new Uri(u).Host : "";
        var parts = host.Split('.');
        return parts.Length >= 2 ? string.Join('.', parts[^2..]) : host;
    }

    private static bool Flag(Dictionary<string, object?> c, string key, bool dflt)
    {
        if (c.TryGetValue("flags", out var f) && f is System.Collections.IDictionary d && d.Contains(key))
        {
            var v = d[key];
            if (v is bool b) return b;
            if (bool.TryParse(v?.ToString(), out var pb)) return pb;
        }
        return dflt;
    }

    // Builds the seed-config + restart command. Heredocs are UNQUOTED so $SECRET
    // expands (config.yml); the Traefik rule backticks are escaped (\`) exactly as
    // the stock installer does. host/IP are injected as literals (no shell vars).
    private static string BuildWrite(Shape s, string marker, string host, string url, string baseDomain, string edge, string localIp)
    {
        var c = s.Spec.Config;
        string B(bool x) => x ? "true" : "false";

        var cfg = new List<string>
        {
            $"# homelab-managed: {marker}",
            "gerbil:",
            "    start_port: 51820",
            $"    base_endpoint: \"{host}\"",
            "app:",
            $"    dashboard_url: \"{url}\"",
            "    log_level: \"info\"",
            "domains:",
            "    domain1:",
            $"        base_domain: \"{baseDomain}\"",
        };
        if (edge != "cloudflared") cfg.Add("        cert_resolver: \"letsencrypt\"");
        cfg.AddRange(new[]
        {
            "server:",
            "    secret: \"$SECRET\"",
            "flags:",
            "    require_email_verification: false",
            $"    disable_signup_without_invite: {B(Flag(c, "disableSignupWithoutInvite", true))}",
            $"    disable_user_create_org: {B(Flag(c, "disableUserCreateOrg", false))}",
            $"    allow_raw_resources: {B(Flag(c, "allowRawResources", true))}",
            $"    enable_integration_api: {B(Flag(c, "enableIntegrationApi", false))}",
        });

        var sb = new StringBuilder();
        sb.Append("set -e\n");
        sb.Append("cd /opt/pangolin/config\n");
        sb.Append("SECRET=$(grep -m1 -oP 'secret:[[:space:]]*\"\\K[^\"]+' config.yml 2>/dev/null || true)\n");
        sb.Append("if [ -z \"$SECRET\" ]; then SECRET=$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32); fi\n");
        sb.Append("cat > config.yml <<EOF\n").Append(string.Join("\n", cfg)).Append("\nEOF\n");

        if (edge == "cloudflared")
        {
            // All routers on the plain-HTTP `web` entrypoint; no TLS, no redirect.
            var dyn = string.Join("\n", new[]
            {
                "http:",
                "  routers:",
                "    next-router:",
                $"      rule: \"Host(\\`{host}\\`) && !PathPrefix(\\`/api/v1\\`)\"",
                "      service: next-service",
                "      entryPoints:",
                "        - web",
                "    api-router:",
                $"      rule: \"Host(\\`{host}\\`) && PathPrefix(\\`/api/v1\\`)\"",
                "      service: api-service",
                "      entryPoints:",
                "        - web",
                "    ws-router:",
                $"      rule: \"Host(\\`{host}\\`)\"",
                "      service: api-service",
                "      entryPoints:",
                "        - web",
                "  services:",
                "    next-service:",
                "      loadBalancer:",
                "        servers:",
                $"          - url: \"http://{localIp}:3002\"",
                "    api-service:",
                "      loadBalancer:",
                "        servers:",
                $"          - url: \"http://{localIp}:3000\"",
            });
            sb.Append("cat > traefik/dynamic_config.yml <<DYN\n").Append(dyn).Append("\nDYN\n");
        }
        sb.Append("systemctl restart pangolin gerbil");
        return sb.ToString();
    }
}
