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
    SecretDeriver Deriver,
    // Optional progress sink for provisioners whose apply takes minutes. Without it a long
    // apply is indistinguishable from a hung one from the outside — which is how a working
    // converge came to be cancelled at sixteen minutes (#369). Optional and last, so every
    // existing construction keeps compiling.
    Action<string>? Progress = null)
{
    public void Report(string message) => Progress?.Invoke(message);
}

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
        new PodmanProvisioner(),
        new ShellProvisioner(),
        new QbittorrentProvisioner(),
        new ProwlarrProvisioner(),
        new SonarrProvisioner(),
        new RadarrProvisioner(),
        new BazarrProvisioner(),
        new CrossSeedProvisioner(),
        new ShelfmarkProvisioner(),
        new SeerrProvisioner(),
        new PlexProvisioner(),
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
            yield return $"ensure tunnel '{tunnel}' + DNS exist (hand-managed records untouched; CLAUDE.md)";
            yield return $"apply {ingressCount} ingress rule(s); install tunnel token; start cloudflared";
            var allow = ParseAccessAllow(s.Spec.Config);
            if (allow.Count > 0)
                yield return $"ensure a CF Access OTP app per gated hostname (allow {string.Join(", ", allow)})";
            yield return "reconcile: prune our managed CNAMEs + Access apps that left the shape (#195)";
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

        // Zones are resolved PER HOSTNAME, not once from ingress[0] (#322). A single tunnel
        // may legitimately front hostnames in more than one zone — the household media path
        // lives in `tao-simon.family` while the rest of the stack is `chrison.dev`. The old
        // code took ingress[0]'s zone and used its ZoneId for EVERY DNS call, so a
        // second-zone hostname would have had its CNAME looked up (and created) in the WRONG
        // zone: the existence check always misses, and the create either fails or plants a
        // record in the first zone. Nothing caught it because no shape had ever mixed zones.
        //
        // AccountId is deliberately still taken once — tunnels and Access apps are
        // account-level, and every zone we manage is in the same account.
        var zonesByName = new Dictionary<string, CfZone>(StringComparer.OrdinalIgnoreCase);
        foreach (var zn in ingress.Select(i => ZoneNameOf(i.host)).Distinct(StringComparer.OrdinalIgnoreCase))
            zonesByName[zn] = await api.GetZoneAsync(zn, ct);
        CfZone ZoneOf(string host) => zonesByName[ZoneNameOf(host)];

        var zone = ZoneOf(ingress[0].host);   // account-level ops only
        var tunnelId = await api.FindTunnelIdAsync(zone.AccountId, tunnelName, ct);
        var svcActive = (await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active cloudflared")).Stdout.Trim() == "active";

        var dnsPresent = true;
        foreach (var (host, _, _) in ingress)
            if (!await api.DnsExistsAsync(ZoneOf(host).ZoneId, host, ct)) { dnsPresent = false; break; }

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
        var bypassIps = ParseAccessBypass(s.Spec.Config);    // config.access.bypass — IP CIDRs that skip OTP
        var publicHosts = ParsePublicHosts(s.Spec.Config);   // ingress entries with public: true
        // Access apps WE create are named "<sub> (<Stack>)" — the suffix scopes the prune
        // (#195) to this stack's apps, never the hand-managed ones (e.g. Core's pdm/proxmox).
        var stack = s.Metadata.Stack ?? "";
        var gated = 0;
        foreach (var (host, _, _) in ingress)
        {
            if (allowEmails.Count == 0 && bypassIps.Count == 0) break;
            // public: true → DNS + tunnel routing but NO Access gate (e.g. audiobookshelf,
            // whose native mobile apps can't do One-Time PIN; it carries its own auth).
            if (publicHosts.Contains(host)) continue;
            var appId = await api.FindAccessAppIdAsync(zone.AccountId, host, ct);
            if (appId is null)
            {
                appId = await api.CreateAccessAppAsync(zone.AccountId, $"{host.Split('.')[0]} ({stack})", host, "24h", ct);
                gated++;
            }
            if (allowEmails.Count > 0 && !await api.AccessPolicyExistsAsync(zone.AccountId, appId, AccessPolicyName, ct))
            {
                await api.CreateAccessAllowEmailPolicyAsync(zone.AccountId, appId, AccessPolicyName, allowEmails, ct);
                gated++;
            }
            // Trusted-IP bypass (e.g. home static IP): skip OTP from there. The app's own
            // login still applies. ADD-ONLY + idempotent by policy name.
            if (bypassIps.Count > 0 && !await api.AccessPolicyExistsAsync(zone.AccountId, appId, BypassPolicyName, ct))
            {
                await api.CreateAccessBypassIpPolicyAsync(zone.AccountId, appId, BypassPolicyName, bypassIps, ct);
                gated++;
            }
        }
        var gateNote = allowEmails.Count > 0 ? $", {ingress.Count} host(s) Access-gated" : "";

        // Declarative reconcile (#195): a converge makes live CF state match the shape, so
        // hostnames removed from the shape must not regress on the next run. ADD-ONLY still
        // holds for HAND-managed resources — we only ever delete (a) a CNAME carrying OUR
        // managed comment that points at THIS tunnel, and (b) an Access app WE named
        // "<sub> (<Stack>)". Anything we didn't create (other comment / other name) is left
        // alone. Decisions are pure (CnamesToPrune/AccessAppsToPrune) + tested; execution
        // is deferred until after the desired state is in place, below.
        var shapeHosts = ingress.Select(i => i.host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gatedHosts = ingress.Where(i => !publicHosts.Contains(i.host)).Select(i => i.host)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Pruned per zone, and the zone is carried alongside each record — DeleteDnsRecord
        // needs the zone the record actually lives in, not ingress[0]'s.
        var cnamesToPrune = new List<(string zoneId, CfDnsRecord rec)>();
        if (tunnelId is not null)
            foreach (var z in zonesByName.Values.DistinctBy(z => z.ZoneId))
                foreach (var rec in CnamesToPrune(await api.ListCnamesAsync(z.ZoneId, ct), tunnelId, shapeHosts))
                    cnamesToPrune.Add((z.ZoneId, rec));
        var appsToPrune = tunnelId is null || string.IsNullOrEmpty(stack)
            ? new List<CfAccessApp>()
            : AccessAppsToPrune(await api.ListAccessAppsAsync(zone.AccountId, ct), $" ({stack})", gatedHosts);
        var pruneCount = cnamesToPrune.Count + appsToPrune.Count;

        // Idempotency: tunnel + all DNS + active connector + gating + ingress + nothing-to-prune → done.
        if (tunnelId is not null && dnsPresent && svcActive && gated == 0 && !ingressDrift && pruneCount == 0)
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
            if (!await api.DnsExistsAsync(ZoneOf(host).ZoneId, host, ct))
                await api.CreateCnameAsync(ZoneOf(host).ZoneId, host, $"{tunnelId}.cfargotunnel.com", ct);

        // Prune what left the shape (decided read-only above) now the desired state is in place.
        foreach (var (zid, rec) in cnamesToPrune) await api.DeleteDnsRecordAsync(zid, rec.Id, ct);
        foreach (var app in appsToPrune) await api.DeleteAccessAppAsync(zone.AccountId, app.Id, ct);

        var changes = (gated > 0 ? $"; {gated} Access change(s)" : "") + (ingressDrift ? "; ingress re-pushed" : "")
            + (pruneCount > 0 ? $"; pruned {cnamesToPrune.Count} CNAME(s) + {appsToPrune.Count} Access app(s)" : "");
        return ApplyResult.Applied($"tunnel '{tunnelName}' ensured ({ingress.Count} ingress){changes}");
    }

    // Registrable domain for a hostname — the zone a DNS record belongs in.
    // Last two labels: seerr.chrison.dev → chrison.dev, seerr.tao-simon.family →
    // tao-simon.family. This is the pre-existing heuristic, kept deliberately: it is wrong
    // for multi-part public suffixes (a .co.nz zone would resolve to "co.nz" and the
    // GetZoneAsync lookup would throw "zone not visible to token" rather than silently
    // misfile the record). No homelab hostname uses one today.
    internal static string ZoneNameOf(string host)
    {
        var parts = host.Split('.');
        return parts.Length <= 2 ? host : string.Join('.', parts[^2..]);
    }

    private const string AccessPolicyName = "allow-homelab-admins";
    private const string BypassPolicyName = "bypass-trusted-ip";

    // --- Pure prune decisions (#195) — no I/O, so the rules are unit-tested directly. ---

    // CNAMEs to delete: ours (managed comment) AND pointing at THIS tunnel AND whose host
    // is no longer in the shape's ingress. The comment + tunnel-content guard means a
    // hand-managed record, or one of ours pointing at a DIFFERENT tunnel (e.g. a hostname
    // migrated onto another stack's tunnel), is never touched — only true orphans go.
    public static List<CfDnsRecord> CnamesToPrune(
        IEnumerable<CfDnsRecord> live, string tunnelId, IReadOnlySet<string> shapeHosts)
    {
        var tunnelContent = $"{tunnelId}.cfargotunnel.com";
        return live.Where(r =>
            r.Comment == CloudflareApi.ManagedComment
            && string.Equals(r.Content, tunnelContent, StringComparison.OrdinalIgnoreCase)
            && !shapeHosts.Contains(r.Name)).ToList();
    }

    // Access apps to delete: ours (name ends with the stack suffix " (<Stack>)") AND whose
    // domain is no longer a GATED shape host (an ingress host that isn't public: true). So
    // removing a host from the shape, or flipping it to public, retires its Access app;
    // hand-managed apps (different name) are never matched.
    public static List<CfAccessApp> AccessAppsToPrune(
        IEnumerable<CfAccessApp> live, string stackSuffix, IReadOnlySet<string> gatedHosts)
        => live.Where(a =>
            a.Name.EndsWith(stackSuffix, StringComparison.Ordinal)
            && !gatedHosts.Contains(a.Domain)).ToList();

    // Ingress hostnames flagged `public: true` — routed by the tunnel but deliberately
    // NOT placed behind the CF Access OTP gate (the app provides its own auth and/or has
    // native clients that can't satisfy OTP). ADD-ONLY: we simply skip gating these.
    private static HashSet<string> ParsePublicHosts(Dictionary<string, object?> c)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (c.TryGetValue("ingress", out var v) && v is IEnumerable<object> items)
            foreach (var it in items)
                if (it is System.Collections.IDictionary d
                    && d["public"] is { } pub && bool.TryParse(pub.ToString(), out var b) && b
                    && d["hostname"]?.ToString() is { Length: > 0 } h)
                    hosts.Add(h);
        return hosts;
    }

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

    // config.access.bypass — IP CIDRs whose requests skip the OTP gate entirely.
    private static List<string> ParseAccessBypass(Dictionary<string, object?> c)
    {
        var cidrs = new List<string>();
        if (c.TryGetValue("access", out var a) && a is System.Collections.IDictionary ad
            && ad["bypass"] is IEnumerable<object> items)
            foreach (var it in items)
                if (it?.ToString() is { Length: > 0 } ip) cidrs.Add(ip);
        return cidrs;
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
                        // YamlDotNet hands us scalars as STRINGS — so noTLSVerify:true arrives
                        // as "true". Cloudflare's ingress schema needs a real bool/number, so
                        // coerce before serializing (else the re-push 400s with code 1056).
                        var map = new Dictionary<string, object?>();
                        foreach (System.Collections.DictionaryEntry e in od)
                            map[e.Key?.ToString() ?? ""] = CoerceScalar(e.Value);
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

    // YAML scalars arrive as strings (YamlDotNet); coerce "true"/"false" → bool and
    // integers → long so the Cloudflare ingress JSON carries the types its schema wants
    // (originRequest.noTLSVerify is a bool; connectTimeout etc. are numbers). Exposed for tests.
    internal static object? CoerceScalar(object? v)
    {
        if (v is null or bool or int or long or double) return v;
        var s = v.ToString();
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (long.TryParse(s, out var l)) return l;
        return s;
    }
}

// Pangolin — SSO remote-access reverse proxy (ADR-0007). Two edge modes:
//
//  • edge: public-wildcard (the ACCEPTED end state) — Docker ENTERPRISE EDITION
//    (fosrl/pangolin:ee, OIDC/RBAC need the EE license; the native OSS install can't
//    activate one — #168). The CT is a Docker host (app: docker → ct/docker.sh) and
//    this provisioner RENDERS the compose (pangolin/gerbil/traefik) + config + Traefik
//    config and runs `docker compose up -d`. Traefik OWNS public :443 with Let's
//    Encrypt WILDCARD certs (DNS-01 via Cloudflare) for *.lab / *.arr, reached over a
//    home-IP :443 port-forward. The dashboard (pangolin.chrison.dev) still comes via the
//    core CF tunnel on plain :80, so only the Traefik STATIC config is new; resource
//    routers are injected by Pangolin's own HTTP provider (pangolin:3001).
//
//  • edge: cloudflared (legacy / rollback) — the native systemd OSS install behind the
//    core tunnel; every Traefik router on plain-HTTP :80, cloudflared provides TLS.
//
// Both GENERATE + PRESERVE server.secret on the CT (no manual secrets.env step) and are
// idempotent via a managed marker stamped into config.yml. EE license activation is a
// one-time manual /admin/license step (UI-only — no API/env path).
public sealed class PangolinProvisioner : IAppProvisioner
{
    public string App => "pangolin";

    // Docker EE defaults (public-wildcard mode). The image/version pins are the pieces
    // to confirm during the live rollout (Phase 3) — all overridable via spec.config.
    internal const string DefaultImage = "fosrl/pangolin:ee-1.19.4";
    internal const string DefaultGerbilImage = "fosrl/gerbil:1.1.0";
    internal const string DefaultTraefikImage = "traefik:v3.6";
    internal const string DefaultBadgerVersion = "v1.2.0";
    internal const string PublicWildcard = "public-wildcard";
    private const string LeProd = "https://acme-v02.api.letsencrypt.org/directory";
    private const string LeStaging = "https://acme-staging-v02.api.letsencrypt.org/directory";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var url = s.Spec.Config.Str("dashboardUrl") ?? "(dashboardUrl)";
        var edge = s.Spec.Config.Str("edge") ?? "cloudflared";
        if (edge == PublicWildcard)
        {
            var img = s.Spec.Config.Str("image") ?? DefaultImage;
            yield return $"render /opt/pangolin/{{compose.yml,.env,config/*}} — Docker EE {img}; generate+preserve server.secret";
            yield return $"Traefik owns :443 with LE wildcard certs (DNS-01 via Cloudflare) for {string.Join(" + ", WildcardFqdns(s))}";
            yield return "docker compose up -d (idempotent via managed marker) — then activate EE once at /admin/license (manual)";
            if (s.Spec.Config.Str("publicIp") is { Length: > 0 } pip)
                yield return $"ensure grey-cloud A record(s) {string.Join(" + ", WildcardFqdns(s))} → {pip} (add-only)";
        }
        else
        {
            yield return $"seed /opt/pangolin/config/config.yml (dashboard_url {url}; generate+preserve server.secret; flags)";
            yield return edge == "cloudflared"
                ? "rewrite traefik/dynamic_config.yml for behind-cloudflared (HTTP :80, no Let's Encrypt, no https-redirect)"
                : $"edge '{edge}': leave stock Traefik (Let's Encrypt public ingress)";
            yield return "restart pangolin + gerbil if config changed (idempotent via managed marker)";
        }
        yield return "reconcile declared resources via the integration API (add-only by fullDomain)";
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
            Flag(c, "enableIntegrationApi", false),
            // public-wildcard inputs — a change to the image/versions/zones/ACME server
            // re-renders the compose + Traefik config on the next converge.
            c.Str("image") ?? DefaultImage,
            c.Str("gerbilImage") ?? DefaultGerbilImage,
            c.Str("traefikImage") ?? DefaultTraefikImage,
            c.Str("badgerVersion") ?? DefaultBadgerVersion,
            c.Str("letsEncryptEmail") ?? "",
            CBool(c, "leStaging", false),
            string.Join(",", WildcardZones(s)),
            // additional base domains change both config.yml's `domains:` block and Traefik's
            // TLS SAN list, so they must re-render the deploy (#322).
            string.Join(",", AdditionalDomains(s)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
    }

    // The wildcard zones (e.g. ["lab","arr"]) — from config.wildcardZones, else derived
    // from the distinct zones declared on resources[]. Drives the LE wildcard SANs.
    internal static List<string> WildcardZones(Shape s)
    {
        var c = s.Spec.Config;
        var zones = new List<string>();
        if (c.TryGetValue("wildcardZones", out var wz) && wz is IEnumerable<object> items)
            foreach (var z in items) if (z?.ToString() is { Length: > 0 } zs && !zones.Contains(zs)) zones.Add(zs);
        if (zones.Count == 0 && c.TryGetValue("resources", out var rv) && rv is IEnumerable<object> res)
            foreach (var it in res)
                if (it is System.Collections.IDictionary rd && rd["zone"]?.ToString() is { Length: > 0 } z && !zones.Contains(z))
                    zones.Add(z);
        return zones;
    }

    // The wildcard FQDNs requested as LE SANs, e.g. *.lab.chrison.dev.
    internal static List<string> WildcardFqdns(Shape s)
    {
        var b = BaseDomain(s);
        var l = WildcardZones(s).Select(z => $"*.{z}.{b}").ToList();
        // An additional domain is fronted at its own apex, so its wildcard is one level up
        // (*.tao-simon.family, not *.<zone>.tao-simon.family) — those domains carry a handful
        // of household hostnames, not a zoned admin surface.
        l.AddRange(AdditionalDomains(s).Select(d => $"*.{d}"));
        return l;
    }

    // Every base domain this Pangolin fronts: config.baseDomain first (it stays `domain1`, so
    // existing resources keep their domainId), then config.additionalDomains in order.
    //
    // Pangolin has no API to create a domain — `GET /org/{id}/domains` is read-only — so the
    // set is whatever config.yml declares, and config.yml is rendered here. That is why adding
    // a second domain was an engine change rather than a config edit (#322).
    internal static List<string> AllDomains(Shape s)
    {
        var list = new List<string> { BaseDomain(s) };
        list.AddRange(AdditionalDomains(s));
        return list;
    }

    internal static List<string> AdditionalDomains(Shape s)
    {
        var extra = new List<string>();
        if (s.Spec.Config.TryGetValue("additionalDomains", out var ad) && ad is IEnumerable<object> items)
            foreach (var d in items)
                if (d?.ToString() is { Length: > 0 } ds
                    && !ds.Equals(BaseDomain(s), StringComparison.OrdinalIgnoreCase)
                    && !extra.Contains(ds, StringComparer.OrdinalIgnoreCase))
                    extra.Add(ds);
        return extra;
    }

    // The grey-cloud A records public-wildcard mode declares (#221): one per wildcard SAN,
    // *.<zone>.<baseDomain> → the home WAN IP (config.publicIp). Each LE wildcard cert Traefik
    // mints is useless unless the hostname actually resolves to the home :443 port-forward.
    // Empty in cloudflared mode (no public :443) or when publicIp is unset.
    internal static IReadOnlyList<(string Fqdn, string Ip)> WildcardARecords(Shape s)
    {
        if ((s.Spec.Config.Str("edge") ?? "cloudflared") != PublicWildcard) return Array.Empty<(string, string)>();
        if (s.Spec.Config.Str("publicIp") is not { Length: > 0 } ip) return Array.Empty<(string, string)>();
        return WildcardFqdns(s).Select(f => (f, ip)).ToList();
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");
        if (s.Spec.Config.Str("dashboardUrl") is not { } dashboardUrl) return ApplyResult.Failed("no dashboardUrl in config");
        var host = new Uri(dashboardUrl).Host;
        var edge = s.Spec.Config.Str("edge") ?? "cloudflared";
        var marker = DesiredMarker(s);

        // Re-deploy only when the managed marker drifted. public-wildcard stamps the marker
        // to a dedicated file as the LAST deploy step (mark-on-SUCCESS) — so a partial failure
        // (e.g. image pull) leaves no "current" marker and the next converge re-runs. The
        // legacy native path keeps the marker in config.yml's first line.
        string markerRead = edge == PublicWildcard
            ? "cat /opt/pangolin/.homelab-managed 2>/dev/null || true"
            : "grep -m1 '^# homelab-managed:' /opt/pangolin/config/config.yml 2>/dev/null || true";
        var cur = await ctx.Exec.InContainerAsync(node, ctid, markerRead);
        var curMarker = edge == PublicWildcard
            ? cur.Stdout.Trim()
            : (cur.Stdout.Contains(':') ? cur.Stdout.Split(':', 2)[1].Trim() : "");
        string? configMsg = null;
        if (curMarker != marker)
        {
            string cmd, mode;
            if (edge == PublicWildcard)
            {
                // Docker EE: Traefik reaches the app by compose service name (no CT IP needed).
                // The Cloudflare DNS token (for Traefik's DNS-01 wildcard challenge) is required.
                var cfToken = ctx.Secrets.Get("CF_DNS_API_TOKEN") ?? ctx.Secrets.Get("CF_API_TOKEN");
                if (string.IsNullOrEmpty(cfToken))
                    return ApplyResult.Failed("public-wildcard needs CF_DNS_API_TOKEN (or CF_API_TOKEN) for Traefik's DNS-01 challenge");
                cmd = BuildDockerDeploy(s, marker, host, dashboardUrl, BaseDomain(s), cfToken);
                mode = "rendered compose + config + LE-wildcard Traefik; docker compose up";
            }
            else
            {
                var ipRes = await ctx.Exec.InContainerAsync(node, ctid, "hostname -I | awk '{print $1}'");
                if (!ipRes.Ok || ipRes.Stdout.Trim().Length == 0) return ApplyResult.Failed("could not resolve CT IP");
                cmd = BuildWrite(s, marker, host, dashboardUrl, BaseDomain(s), edge, ipRes.Stdout.Trim());
                mode = $"seeded config.yml + {(edge == "cloudflared" ? "behind-cloudflared Traefik" : "stock Traefik")}; restarted";
            }
            var res = await ctx.Exec.InContainerAsync(node, ctid, cmd);
            if (!res.Ok) return ApplyResult.Failed($"write/deploy failed: {res.Stderr}");
            configMsg = $"{mode} (marker {marker})";
        }

        // Wildcard DNS (#221): in public-wildcard mode, declare the grey-cloud A records that
        // point each *.<zone> hostname at the home WAN IP. Add-only, idempotent by existence.
        // Skipped — not failed — when publicIp or the CF token is absent (so a plan/dev run
        // without the WAN IP or creds still converges the rest).
        var (dnsMsg, dnsChanged, dnsFailed) = await ReconcileWildcardDnsAsync(s, ctx);
        if (dnsFailed is not null) return ApplyResult.Failed(dnsFailed);

        // Resources (declarative, #136): reconcile declared admin-UI resources via the
        // integration API (add-only, idempotent by fullDomain). Skipped — not failed —
        // until the org PANGOLIN_API_KEY exists, since that's a post-setup bootstrap secret.
        var (resMsg, resChanged, resFailed) = await ReconcileResourcesAsync(s, ctx, node, ctid);
        if (resFailed is not null) return ApplyResult.Failed(resFailed);

        if (configMsg is null && !dnsChanged && !resChanged)
            return ApplyResult.NoChange($"config current (marker {marker})"
                + (dnsMsg is null ? "" : $"; {dnsMsg}") + (resMsg is null ? "" : $"; {resMsg}"));
        return ApplyResult.Applied(string.Join("; ", new[] { configMsg, dnsMsg, resMsg }.Where(x => x is not null)));
    }

    // Reconcile the grey-cloud wildcard A records (#221): one proxied:false A record per
    // wildcard zone, *.<zone>.<baseDomain> → the home WAN IP, so the LE wildcard hostnames
    // resolve to the home :443 port-forward. Add-only + ManagedComment-stamped (so #195's
    // prune leaves hand-managed records alone), idempotent by existence. Skipped — not failed —
    // when publicIp or the CF token is absent. Returns (msg, changed, failedReason).
    private static async Task<(string? msg, bool changed, string? failed)> ReconcileWildcardDnsAsync(Shape s, ConvergeContext ctx)
    {
        if ((s.Spec.Config.Str("edge") ?? "cloudflared") != PublicWildcard) return (null, false, null);
        if (WildcardFqdns(s).Count == 0) return (null, false, null);

        var want = WildcardARecords(s);   // gated on edge=public-wildcard + publicIp set
        if (want.Count == 0)
            return ("wildcard zone(s) declared but config.publicIp unset — DNS skipped (set the home WAN IP, then re-run)", false, null);
        // Traefik already needs this token for its DNS-01 challenge, so it's the same secret.
        if ((ctx.Secrets.Get("CF_DNS_API_TOKEN") ?? ctx.Secrets.Get("CF_API_TOKEN")) is not { Length: > 0 } token)
            return ("wildcard A record(s) declared but CF_DNS_API_TOKEN/CF_API_TOKEN unset — DNS skipped", false, null);

        var ct = CancellationToken.None;
        var api = new CloudflareApi(token);
        var zone = await api.GetZoneAsync(BaseDomain(s), ct);
        int created = 0;
        foreach (var (fqdn, ip) in want)
            if (!await api.DnsExistsAsync(zone.ZoneId, fqdn, ct))
            {
                await api.CreateARecordAsync(zone.ZoneId, fqdn, ip, ct);
                created++;
            }
        return ($"{want.Count} wildcard A record(s) declared, {created} created", created > 0, null);
    }

    // Reconcile declared Pangolin resources (admin UIs) via the integration API on the
    // CT (:3003, Bearer org key). Find-or-create by fullDomain, then RECONCILE — a
    // resource may carry a wildcard `zone` (lab|arr) → fullDomain =
    // <subdomain>.<zone>.<baseDomain>. ssl follows the edge mode: public-wildcard →
    // Traefik terminates TLS (true); cloudflared → CF does (false). sso defaults ON
    // (Pangolin auth gates the UI); a resource opts out with sso: false.
    //
    // WAS ADD-ONLY, AND THAT BROKE PUBLIC ROUTES SILENTLY (#309). Editing a declared
    // resource's target updated desired state and never reached Pangolin: existence was
    // checked by fullDomain and an existing resource was skipped entirely. During the
    // monitoring migration (#303) the Pulse UI moved from CT 4000 to CT 4001 and the
    // shape was updated, but converge reported "0 to create, 0 drifted, 3 up-to-date" —
    // a completely clean plan — while the live target still pointed at 10.10.0.40, a
    // container that had just been stopped. pulse.lab.chrison.dev was down and converge
    // said everything was fine. Now the target (ip/port/method/enabled) and the
    // ssl/sso gates are all compared and corrected.
    //
    // Returns (msg, changed, failedReason).
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
        var publicWildcard = (c.Str("edge") ?? "cloudflared") == PublicWildcard;
        var ct = CancellationToken.None;
        var pg = new PangolinClient(ctx.Exec, node, ctid, key);

        // domainId per base domain (#322). A resource may name a `domain:` other than the
        // default baseDomain; the map is built from what Pangolin actually has registered, so
        // a domain declared in the shape but not yet picked up from config.yml fails loudly
        // on the resource that needs it rather than silently landing on domain1.
        var (dok, droot) = await pg.CallAsync("GET", $"/org/{org}/domains", null, ct);
        if (!dok) return (null, false, "pangolin: integration API unreachable or key invalid (GET domains failed)");
        var domainIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DataArray(droot, "domains"))
            if (d.TryGetProperty("baseDomain", out var bd) && bd.GetString() is { Length: > 0 } bds
                && d.TryGetProperty("domainId", out var di) && di.GetString() is { Length: > 0 } dis)
                domainIds[bds] = dis;
        if (!domainIds.ContainsKey(baseDomain))
            return (null, false, $"pangolin: domain '{baseDomain}' not found in org '{org}'");

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

        // Existing resources by fullDomain. One GET carries resourceId, ssl, sso AND the
        // embedded targets, so the common no-drift case costs a single call.
        var existing = new Dictionary<string, LiveResource>(StringComparer.OrdinalIgnoreCase);
        var (eok, eroot) = await pg.CallAsync("GET", $"/org/{org}/resources", null, ct);
        if (!eok) return (null, false, "pangolin: GET resources failed — cannot reconcile safely");
        foreach (var r in DataArray(eroot, "resources"))
        {
            if (!r.TryGetProperty("fullDomain", out var fd) || fd.GetString() is not { } f) continue;
            if (!r.TryGetProperty("resourceId", out var ri)) continue;
            existing[f] = new LiveResource(
                ri.GetInt32(),
                Truthy(r, "ssl"),
                Truthy(r, "sso"),
                r.TryGetProperty("targets", out var tarr) && tarr.ValueKind == JsonValueKind.Array
                    ? tarr.EnumerateArray().Count() : 0);
        }

        int total = 0, created = 0, retargeted = 0, regated = 0;
        var notes = new List<string>();
        foreach (var it in items)
        {
            if (it is not System.Collections.IDictionary rd) continue;
            total++;
            var sub = rd["subdomain"]?.ToString() ?? "";
            // wildcard zone (lab|arr) → register the resource under subdomain "<sub>.<zone>"
            // so fullDomain = <sub>.<zone>.<baseDomain> (covered by the *.<zone> wildcard cert).
            var zone = rd["zone"]?.ToString();
            var pgSub = string.IsNullOrEmpty(zone) ? sub : $"{sub}.{zone}";
            // `domain:` selects which registered base domain this resource hangs off; omitted
            // means the default baseDomain, so every pre-existing entry is unaffected (#322).
            var rDomain = rd["domain"]?.ToString() is { Length: > 0 } rdm ? rdm : baseDomain;
            if (!domainIds.TryGetValue(rDomain, out var domainId))
            {
                notes.Add($"{sub}: domain '{rDomain}' is not registered in Pangolin — add it to config.additionalDomains and re-converge");
                continue;
            }
            var fqdn = $"{pgSub}.{rDomain}";
            var name = rd["name"]?.ToString() ?? sub;
            var tgt = rd["target"] as System.Collections.IDictionary;
            var tip = tgt?["ip"]?.ToString() ?? "localhost";
            var tmethod = tgt?["method"]?.ToString() ?? "http";
            var tport = int.TryParse(tgt?["port"]?.ToString(), out var pp) ? pp : 80;
            // sso gate: default ON — admin UIs must sit behind Pangolin auth (badger). The
            // integration-API create defaults sso to null (OPEN), so we MUST set it explicitly
            // or the resource is born publicly reachable. A resource may opt out (sso: false)
            // for native clients that can't render the SSO interstitial (e.g. Plex, abs).
            var sso = ResourceSsoEnabled(rd);

            if (existing.TryGetValue(fqdn, out var live))
            {
                // ── EXISTS: reconcile rather than skip (#309) ──────────────────────────
                var (tmsg, tchanged, tfail) = await ReconcileTargetAsync(
                    pg, live, siteId.Value, tip, tmethod, tport, fqdn, ct);
                if (tfail is not null) return (null, false, tfail);
                if (tmsg is not null) notes.Add(tmsg);
                if (tchanged) retargeted++;

                // ssl/sso are cheap to compare (already fetched) and drift the same way.
                // sso especially: a resource created before the default-ON decision (#238),
                // or flipped off by hand in the UI, would otherwise stay open forever.
                if (live.Ssl != publicWildcard || live.Sso != sso)
                {
                    var (gok, _) = await pg.CallAsync("POST", $"/resource/{live.Id}",
                        JsonSerializer.Serialize(new { ssl = publicWildcard, sso }), ct);
                    if (!gok) return (null, false, $"pangolin: failed to update ssl/sso on {fqdn}");
                    notes.Add($"{fqdn}: ssl {live.Ssl}→{publicWildcard}, sso {live.Sso}→{sso}");
                    regated++;
                }
                continue;
            }

            // ── ABSENT: create ────────────────────────────────────────────────────────
            var (rok, rroot) = await pg.CallAsync("PUT", $"/org/{org}/resource",
                JsonSerializer.Serialize(new { name, subdomain = pgSub, http = true, protocol = "tcp", domainId }), ct);
            if (!rok || !Data(rroot).TryGetProperty("resourceId", out var rid))
                return (null, false, $"pangolin: failed to create resource {fqdn}");
            var resourceId = rid.GetInt32();
            await pg.CallAsync("PUT", $"/resource/{resourceId}/target",
                JsonSerializer.Serialize(new { siteId, ip = tip, method = tmethod, port = tport, enabled = true }), ct);
            // ssl: public-wildcard → Traefik terminates TLS (true); cloudflared → CF does (false).
            // sso: gate the resource behind Pangolin auth unless it explicitly opts out.
            await pg.CallAsync("POST", $"/resource/{resourceId}", JsonSerializer.Serialize(new { ssl = publicWildcard, sso }), ct);
            created++;
        }

        var summary = $"{total} resource(s) declared, {created} created, {retargeted} retargeted, {regated} re-gated";
        if (notes.Count > 0) summary += "\n      " + string.Join("\n      ", notes);
        return (summary, created + retargeted + regated > 0, null);
    }

    // What a live Pangolin resource looks like, as far as reconciliation cares.
    // TargetCount comes from the embedded list on GET /org/{org}/resources; the per-target
    // detail needs its own call because that embedded form omits `method`.
    private readonly record struct LiveResource(int Id, bool Ssl, bool Sso, int TargetCount);

    // Bring one resource's target in line with the shape.
    //
    // The embedded target list on GET resources carries ip/port/enabled but NOT `method`,
    // so detail comes from GET /resource/{id}/targets — one extra call per DECLARED and
    // already-existing resource. Comparing only the embedded fields would have left
    // method drift undetectable, i.e. it would reproduce the exact silent-no-op class of
    // bug this issue is about, just narrower.
    //
    // MULTI-TARGET RESOURCES ARE LEFT ALONE. Pangolin supports several targets per
    // resource for load balancing; our shapes only ever declare one. Rewriting the first
    // of several would silently destroy a hand-built config, which the add-only guardrail
    // in CLAUDE.md exists to prevent — so that case reports and skips instead.
    private static async Task<(string? msg, bool changed, string? failed)> ReconcileTargetAsync(
        PangolinClient pg, LiveResource live, int siteId,
        string ip, string method, int port, string fqdn, CancellationToken ct)
    {
        if (live.TargetCount > 1)
            return ($"{fqdn}: {live.TargetCount} targets live (load-balanced?) — left alone, reconcile by hand", false, null);

        if (live.TargetCount == 0)
        {
            var (aok, _) = await pg.CallAsync("PUT", $"/resource/{live.Id}/target",
                JsonSerializer.Serialize(new { siteId, ip, method, port, enabled = true }), ct);
            return aok
                ? ($"{fqdn}: target added → {method}://{ip}:{port}", true, null)
                : (null, false, $"pangolin: failed to add target for {fqdn}");
        }

        var (tok, troot) = await pg.CallAsync("GET", $"/resource/{live.Id}/targets", null, ct);
        if (!tok) return (null, false, $"pangolin: GET targets failed for {fqdn}");
        var t = DataArray(troot, "targets").FirstOrDefault();
        if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("targetId", out var tid))
            return (null, false, $"pangolin: no usable target on {fqdn}");

        var liveIp = t.TryGetProperty("ip", out var ipv) ? ipv.GetString() ?? "" : "";
        var livePort = t.TryGetProperty("port", out var pv) && pv.TryGetInt32(out var pi) ? pi : -1;
        var liveMethod = t.TryGetProperty("method", out var mv) ? mv.GetString() ?? "" : "";
        var liveEnabled = Truthy(t, "enabled");

        if (string.Equals(liveIp, ip, StringComparison.OrdinalIgnoreCase) && livePort == port
            && string.Equals(liveMethod, method, StringComparison.OrdinalIgnoreCase) && liveEnabled)
            return (null, false, null);

        // siteId is REQUIRED on this call even when unchanged — omitting it 400s with
        // 'expected number, received undefined at "siteId"'.
        var (uok, _) = await pg.CallAsync("POST", $"/target/{tid.GetInt32()}",
            JsonSerializer.Serialize(new { siteId, ip, method, port, enabled = true }), ct);
        if (!uok) return (null, false, $"pangolin: failed to update target for {fqdn}");
        return ($"{fqdn}: target {liveMethod}://{liveIp}:{livePort} → {method}://{ip}:{port}", true, null);
    }

    // Pangolin returns SQLite-backed booleans as either true/false or 1/0 depending on
    // the endpoint — `ssl` arrives as a JSON bool while `sso` arrives as an integer. A
    // plain GetBoolean() throws on the latter, so read both shapes.
    private static bool Truthy(JsonElement o, string prop) =>
        o.TryGetProperty(prop, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var i) && i != 0,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
            _ => false,
        };

    // Whether a declared resource is gated by Pangolin auth. Default ON: the integration-API
    // create leaves sso null (OPEN), so a resource is only gated if we set it — this decision
    // is security-relevant (#238). Opt out with sso: false only for native clients (Plex/abs).
    internal static bool ResourceSsoEnabled(System.Collections.IDictionary rd) =>
        !(rd["sso"] is { } raw && bool.TryParse(raw.ToString(), out var v) && !v);

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

    // Top-level config bool (vs Flag(), which reads the Pangolin `flags:` sub-block) —
    // for engine-side knobs like includeGerbil / leStaging.
    private static bool CBool(Dictionary<string, object?> c, string key, bool dflt)
    {
        if (c.TryGetValue(key, out var v))
        {
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
        // Additional domains on the legacy native path too, so a rollback from
        // public-wildcard doesn't silently drop them from config.yml (#322).
        var extraN = 2;
        foreach (var d in AdditionalDomains(s))
        {
            cfg.Add($"    domain{extraN++}:");
            cfg.Add($"        base_domain: \"{d}\"");
            if (edge != "cloudflared") cfg.Add("        cert_resolver: \"letsencrypt\"");
        }
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

    // ── Docker EE public-wildcard deploy (ADR-0007) ─────────────────────────────
    // Renders compose + .env + Traefik config + config.yml onto the CT and runs
    // `docker compose up -d`. config.yml goes via an UNQUOTED heredoc so $SECRET
    // expands (generate-or-preserve, as the native path does); the other artifacts go
    // via base64 -d (quote-safe — Traefik rule backticks need no escaping). Idempotent
    // via the managed marker in config.yml. Image/version pins + the exact gerbil/badger
    // wiring are confirmed live in the rollout (Phase 3); all overridable via config.
    internal static string BuildDockerDeploy(Shape s, string marker, string host, string url, string baseDomain, string cfToken)
    {
        var compose = B64(BuildComposeYaml(s));
        var env = B64($"CF_DNS_API_TOKEN={cfToken}\n");
        var tStatic = B64(BuildTraefikStatic(s, baseDomain));
        var tDynamic = B64(BuildTraefikDynamic(host));
        var cfg = string.Join("\n", BuildConfigLines(s, marker, host, url, baseDomain));

        var sb = new StringBuilder();
        sb.Append("set -e\n");
        // Ensure Docker + compose (the CT is a plain debian base; we install Docker here
        // rather than via ct/docker.sh, whose interactive prompts hang over SSH). Idempotent.
        sb.Append("if ! command -v docker >/dev/null 2>&1; then\n");
        sb.Append("  export DEBIAN_FRONTEND=noninteractive\n");
        sb.Append("  apt-get update -qq && apt-get install -y -qq curl ca-certificates\n");
        sb.Append("  curl -fsSL https://get.docker.com | sh\n");
        sb.Append("fi\n");
        sb.Append("mkdir -p /opt/pangolin/config/traefik /opt/pangolin/config/letsencrypt\n");
        sb.Append("cd /opt/pangolin\n");
        sb.Append("SECRET=$(grep -m1 -oP 'secret:[[:space:]]*\"\\K[^\"]+' config/config.yml 2>/dev/null || true)\n");
        sb.Append("if [ -z \"$SECRET\" ]; then SECRET=$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32); fi\n");
        sb.Append("cat > config/config.yml <<EOF\n").Append(cfg).Append("\nEOF\n");
        sb.Append($"echo {compose} | base64 -d > compose.yml\n");
        sb.Append($"echo {env} | base64 -d > .env && chmod 600 .env\n");
        sb.Append($"echo {tStatic} | base64 -d > config/traefik/traefik_config.yml\n");
        sb.Append($"echo {tDynamic} | base64 -d > config/traefik/dynamic_config.yml\n");
        sb.Append("docker compose up -d\n");
        // Mark-on-SUCCESS: only reached if everything above (incl. compose up) exited 0 under
        // `set -e`. A partial failure leaves no marker → next converge re-runs the deploy.
        sb.Append($"printf '%s' '{marker}' > /opt/pangolin/.homelab-managed");
        return sb.ToString();
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    // config.yml lines (shared shape with the native path; cert_resolver points Pangolin's
    // HTTP-provider routers at Traefik's `letsencrypt` resolver). secret is "$SECRET".
    private static List<string> BuildConfigLines(Shape s, string marker, string host, string url, string baseDomain)
    {
        var c = s.Spec.Config;
        string B(bool x) => x ? "true" : "false";
        var lines = new List<string>
        {
            $"# homelab-managed: {marker}",
            "gerbil:",
            "    start_port: 51820",
            $"    base_endpoint: \"{host}\"",
            "app:",
            $"    dashboard_url: \"{url}\"",
            "    log_level: \"info\"",
            "domains:",
        };
        // domain1 is ALWAYS baseDomain — Pangolin keys resources by domainId, so reordering
        // would re-point every existing resource at the wrong domain.
        lines.AddRange(DomainBlock(1, baseDomain));
        var n = 2;
        foreach (var d in AdditionalDomains(s)) lines.AddRange(DomainBlock(n++, d));
        lines.AddRange(new List<string>
        {
            "server:",
            "    secret: \"$SECRET\"",
            "flags:",
            "    require_email_verification: false",
            $"    disable_signup_without_invite: {B(Flag(c, "disableSignupWithoutInvite", true))}",
            $"    disable_user_create_org: {B(Flag(c, "disableUserCreateOrg", false))}",
            $"    allow_raw_resources: {B(Flag(c, "allowRawResources", true))}",
            $"    enable_integration_api: {B(Flag(c, "enableIntegrationApi", true))}",
        });
        return lines;
    }

    // One `domainN:` stanza. Pangolin registers each as a wildcard domain against the
    // `letsencrypt` resolver; Traefik is what actually mints the cert (DNS-01 via Cloudflare),
    // so the zone must be on the CF token or issuance fails for that domain only.
    private static List<string> DomainBlock(int n, string domain) => new()
    {
        $"    domain{n}:",
        $"        base_domain: \"{domain}\"",
        "        cert_resolver: \"letsencrypt\"",
    };

    // docker-compose.yml. Default: pangolin + traefik (traefik publishes :80/:443
    // directly). includeGerbil=true adds the WireGuard topology (gerbil owns the ports,
    // traefik shares its netns) for the future VPS/Newt graduation (#137) — off by
    // default since WireGuard is unused in the home-IP-port-forward trial.
    internal static string BuildComposeYaml(Shape s)
    {
        var c = s.Spec.Config;
        var image = c.Str("image") ?? DefaultImage;
        var traefik = c.Str("traefikImage") ?? DefaultTraefikImage;
        var gerbil = c.Str("gerbilImage") ?? DefaultGerbilImage;
        var withGerbil = CBool(c, "includeGerbil", false);

        var L = new List<string>
        {
            "services:",
            "  pangolin:",
            $"    image: {image}",
            "    container_name: pangolin",
            "    restart: unless-stopped",
            "    ports:",
            // Integration API on CT-localhost ONLY — the resource reconcile curls
            // localhost:3003 via pct exec (in the CT netns). Not exposed beyond the CT.
            "      - 127.0.0.1:3003:3003",
            "    volumes:",
            "      - ./config:/app/config",
            "    healthcheck:",
            "      test: [\"CMD\", \"curl\", \"-f\", \"http://localhost:3001/api/v1/\"]",
            "      interval: 10s",
            "      timeout: 10s",
            "      retries: 15",
        };
        if (withGerbil)
        {
            L.AddRange(new[]
            {
                "  gerbil:",
                $"    image: {gerbil}",
                "    container_name: gerbil",
                "    restart: unless-stopped",
                "    depends_on:",
                "      pangolin:",
                "        condition: service_healthy",
                "    command:",
                "      - --reachableAt=http://gerbil:3004",
                "      - --generateAndSaveKeyTo=/var/config/key",
                "      - --remoteConfig=http://pangolin:3001/api/v1/gerbil/get-config",
                "      - --reportBandwidthTo=http://pangolin:3001/api/v1/gerbil/receive-bandwidth",
                "    volumes:",
                "      - ./config/:/var/config",
                "    cap_add:",
                "      - NET_ADMIN",
                "      - SYS_MODULE",
                "    ports:",
                "      - 51820:51820/udp",
                "      - 21820:21820/udp",
                "      - 443:443",
                "      - 443:443/udp",
                "      - 80:80",
            });
        }
        L.AddRange(new[]
        {
            "  traefik:",
            $"    image: {traefik}",
            "    container_name: traefik",
            "    restart: unless-stopped",
            "    depends_on:",
            "      pangolin:",
            "        condition: service_healthy",
            "    env_file:",
            "      - .env",
            "    command:",
            "      - --configFile=/etc/traefik/traefik_config.yml",
        });
        // Without gerbil, traefik publishes the public ports itself; with gerbil it shares
        // gerbil's network namespace (gerbil owns the ports above).
        if (withGerbil)
            L.Add("    network_mode: service:gerbil");
        else
            L.AddRange(new[] { "    ports:", "      - 80:80", "      - 443:443" });
        L.AddRange(new[]
        {
            "    volumes:",
            "      - ./config/traefik:/etc/traefik:ro",
            "      - ./config/letsencrypt:/letsencrypt",
        });
        return string.Join("\n", L) + "\n";
    }

    // Traefik STATIC config — Pangolin's HTTP provider (injects resource routers) + file
    // provider (the dashboard) + the badger auth plugin + a `letsencrypt` resolver doing
    // the DNS-01 challenge (Cloudflare) for WILDCARD certs (*.<zone>.<base>), requested up
    // front via websecure's tls.domains so every resource under a zone is covered.
    internal static string BuildTraefikStatic(Shape s, string baseDomain)
    {
        var c = s.Spec.Config;
        var badger = c.Str("badgerVersion") ?? DefaultBadgerVersion;
        var email = c.Str("letsEncryptEmail") ?? "";
        var ca = CBool(c, "leStaging", false) ? LeStaging : LeProd;

        var L = new List<string>
        {
            "api:",
            "  insecure: true",
            "  dashboard: true",
            "providers:",
            "  http:",
            "    endpoint: \"http://pangolin:3001/api/v1/traefik-config\"",
            "    pollInterval: \"5s\"",
            "  file:",
            "    filename: \"/etc/traefik/dynamic_config.yml\"",
            "experimental:",
            "  plugins:",
            "    badger:",
            "      moduleName: \"github.com/fosrl/badger\"",
            $"      version: \"{badger}\"",
            "log:",
            "  level: \"INFO\"",
            "certificatesResolvers:",
            "  letsencrypt:",
            "    acme:",
            "      dnsChallenge:",
            "        provider: cloudflare",
            $"      email: \"{email}\"",
            "      storage: \"/letsencrypt/acme.json\"",
            $"      caServer: \"{ca}\"",
            "entryPoints:",
            "  web:",
            "    address: \":80\"",
            "  websecure:",
            "    address: \":443\"",
            "    http:",
            "      tls:",
            "        certResolver: \"letsencrypt\"",
            "        domains:",
        };
        foreach (var z in WildcardZones(s))
        {
            L.Add($"          - main: \"{z}.{baseDomain}\"");
            L.Add($"            sans: [\"*.{z}.{baseDomain}\"]");
        }
        // Additional domains are fronted at their own apex, so the cert covers the apex plus
        // one wildcard level — no zone segment (#322).
        foreach (var d in AdditionalDomains(s))
        {
            L.Add($"          - main: \"{d}\"");
            L.Add($"            sans: [\"*.{d}\"]");
        }
        L.AddRange(new[] { "serversTransport:", "  insecureSkipVerify: true" });
        return string.Join("\n", L) + "\n";
    }

    // Traefik DYNAMIC config (file provider) — ONLY the dashboard, on plain :80. The
    // dashboard (pangolin.chrison.dev) still arrives via the core CF tunnel (CF provides
    // its TLS), so no certResolver and no badger here (Pangolin's own session gates it;
    // badger would loop on the login UI). Resource routers (with badger + the wildcard
    // cert on :443) are injected separately by Pangolin's HTTP provider.
    internal static string BuildTraefikDynamic(string host)
    {
        var L = new List<string>
        {
            "http:",
            "  routers:",
            "    next-router:",
            $"      rule: \"Host(`{host}`) && !PathPrefix(`/api/v1`)\"",
            "      service: next-service",
            "      entryPoints:",
            "        - web",
            "    api-router:",
            $"      rule: \"Host(`{host}`) && PathPrefix(`/api/v1`)\"",
            "      service: api-service",
            "      entryPoints:",
            "        - web",
            "    ws-router:",
            $"      rule: \"Host(`{host}`)\"",
            "      service: api-service",
            "      entryPoints:",
            "        - web",
            "  services:",
            "    next-service:",
            "      loadBalancer:",
            "        servers:",
            "          - url: \"http://pangolin:3002\"",
            "    api-service:",
            "      loadBalancer:",
            "        servers:",
            "          - url: \"http://pangolin:3000\"",
        };
        return string.Join("\n", L) + "\n";
    }
}
