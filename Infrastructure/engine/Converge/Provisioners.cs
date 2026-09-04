using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        new NewtProvisioner(),
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
        if (OidcSource(s) is { } o)
        {
            yield return $"reconcile the '{o.Name}' OAuth2 auth source (add-oauth / update-oauth, idempotent by name)";
            yield return $"set [oauth2_client] ACCOUNT_LINKING={o.AccountLinking}, USERNAME={o.UsernameClaim}";
        }
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
        string? rootMsg = null;
        if (Norm(current) == Norm(desired))
        {
            // ROOT_URL being current says nothing about the auth source, which lives in the
            // database and can drift or vanish independently. Fall through rather than
            // returning — an early exit here would make the OIDC source unmanaged on every
            // run after the first, which is the shape of bug that cost a day elsewhere.
            if (OidcSource(s) is null) return ApplyResult.NoChange($"ROOT_URL already {current}");
            return await FinishAsync(s, ctx, node, ctid, null);
        }

        var host = new Uri(desired).Host;
        var set =
            $"sed -i \"s|^ROOT_URL = .*|ROOT_URL = {desired}|\" /etc/forgejo/app.ini && " +
            $"sed -i \"s|^DOMAIN = .*|DOMAIN = {host}|\" /etc/forgejo/app.ini && " +
            "systemctl restart forgejo";
        var res = await ctx.Exec.InContainerAsync(node, ctid, set);
        if (!res.Ok) return ApplyResult.Failed($"set failed: {res.Stderr}");
        rootMsg = $"ROOT_URL {current} → {desired} (restarted)";

        return await FinishAsync(s, ctx, node, ctid, rootMsg);
    }

    // ── OIDC auth source (#485) ───────────────────────────────────────────────────────
    //
    // Forgejo keeps auth sources in its DATABASE, not app.ini, so this cannot be a config
    // rewrite like ROOT_URL — it goes through the admin CLI. `add-oauth` creates and
    // `update-oauth --id` edits, so the reconcile is: list, match by name, branch.
    //
    // THE CLIENT SECRET IS NOT READABLE BACK from `auth list`, which prints only
    // ID/Name/Type/Enabled. So drift on the secret is undetectable and the update runs
    // unconditionally when the source exists — cheap, and it makes rotation work by simply
    // changing the value in Secrets Manager. The same limitation the Pangolin IdP reconciler
    // has, handled the opposite way because here the update costs nothing.
    internal readonly record struct ForgejoOidc(
        string Name, string DiscoveryUrl, string ClientIdFrom, string ClientSecretFrom,
        string GroupClaim, string AdminGroup, string Scopes, string AccountLinking, string UsernameClaim);

    internal static ForgejoOidc? OidcSource(Shape s)
    {
        if (!(s.Spec.Config.TryGetValue("oidc", out var v) && v is System.Collections.IDictionary d)) return null;
        string? Str(string k) => d[k]?.ToString() is { Length: > 0 } x ? x : null;
        if (Str("discoveryUrl") is not { } disco) return null;
        return new ForgejoOidc(
            Str("name") ?? "authentik", disco,
            Str("clientIdFrom") ?? "", Str("clientSecretFrom") ?? "",
            Str("groupClaim") ?? "groups", Str("adminGroup") ?? "",
            Str("scopes") ?? "openid profile email",
            Str("accountLinking") ?? "auto",
            Str("usernameClaim") ?? "preferred_username");
    }

    // Built as a pure function so the flag set is testable without a Forgejo to run it on.
    // `--scopes` is REPEATED, not space-joined: the flag is declared as a string slice, and a
    // single "openid profile email" would be stored as one scope of that literal name and
    // silently request nothing useful.
    internal static string BuildAuthCommand(ForgejoOidc o, string clientId, string clientSecret, int? existingId)
    {
        var verb = existingId is { } id ? $"update-oauth --id {id}" : "add-oauth";
        var sb = new StringBuilder($"/usr/local/bin/forgejo admin auth {verb} --config /etc/forgejo/app.ini");
        sb.Append($" --name '{o.Name}' --provider openidConnect");
        sb.Append($" --key '{clientId}' --secret '{clientSecret}'");
        sb.Append($" --auto-discover-url '{o.DiscoveryUrl}'");
        foreach (var scope in o.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            sb.Append($" --scopes '{scope}'");
        if (o.GroupClaim.Length > 0) sb.Append($" --group-claim-name '{o.GroupClaim}'");
        if (o.AdminGroup.Length > 0) sb.Append($" --admin-group '{o.AdminGroup}'");
        return sb.ToString();
    }

    // ID for an existing source of this name, from `auth list`'s tab-separated rows.
    internal static int? ParseAuthSourceId(string listOutput, string name)
    {
        foreach (var line in listOutput.Split('\n'))
        {
            var cols = line.Split('\t', StringSplitOptions.TrimEntries);
            if (cols.Length >= 2 && cols[1].Equals(name, StringComparison.Ordinal)
                && int.TryParse(cols[0], out var id)) return id;
        }
        return null;
    }

    private static async Task<ApplyResult> FinishAsync(
        Shape s, ConvergeContext ctx, string node, string ctid, string? rootMsg)
    {
        if (OidcSource(s) is not { } o) return rootMsg is null
            ? ApplyResult.NoChange("no rootUrl configured")
            : ApplyResult.Applied(rootMsg);

        var clientId = ctx.Secrets.Get(o.ClientIdFrom);
        var clientSecret = ctx.Secrets.Get(o.ClientSecretFrom);
        if (clientId is not { Length: > 0 } || clientSecret is not { Length: > 0 })
            return Combine(rootMsg, $"oidc source '{o.Name}' declared but {o.ClientIdFrom}/{o.ClientSecretFrom} unset — skipped");

        var asGit = $"su - git -s /bin/sh -c \"{{0}}\"";
        var list = await ctx.Exec.InContainerAsync(node, ctid,
            string.Format(asGit, "/usr/local/bin/forgejo admin auth list --config /etc/forgejo/app.ini"));
        if (!list.Ok) return ApplyResult.Failed($"could not list forgejo auth sources: {list.Stderr}");

        var existing = ParseAuthSourceId(list.Stdout, o.Name);
        var cmd = BuildAuthCommand(o, clientId, clientSecret, existing);
        var run = await ctx.Exec.InContainerAsync(node, ctid, string.Format(asGit, cmd.Replace("\"", "\\\"")));
        // The secret is in that command line; never echo stderr verbatim on failure.
        if (!run.Ok) return ApplyResult.Failed(
            $"forgejo auth {(existing is null ? "add" : "update")}-oauth failed for '{o.Name}' (output withheld — the command carries the client secret)");

        // [oauth2_client] governs what happens AFTER a successful assertion. Written here
        // rather than in the shape's app.ini edits because it is meaningless without a source.
        var ini = string.Join(" && ", new[]
        {
            "grep -q '^\\[oauth2_client\\]' /etc/forgejo/app.ini || printf '\\n[oauth2_client]\\n' >> /etc/forgejo/app.ini",
            $"sed -i '/^\\[oauth2_client\\]/,/^\\[/{{/^ACCOUNT_LINKING/d;/^USERNAME/d;/^ENABLE_AUTO_REGISTRATION/d}}' /etc/forgejo/app.ini",
            $"sed -i '/^\\[oauth2_client\\]/a ENABLE_AUTO_REGISTRATION = false\\nACCOUNT_LINKING = {o.AccountLinking}\\nUSERNAME = {o.UsernameClaim}' /etc/forgejo/app.ini",
            "systemctl restart forgejo",
        });
        var iniRes = await ctx.Exec.InContainerAsync(node, ctid, ini);
        if (!iniRes.Ok) return ApplyResult.Failed($"could not write [oauth2_client]: {iniRes.Stderr}");

        return Combine(rootMsg,
            $"oidc source '{o.Name}' {(existing is null ? "created" : "updated")}"
            + (o.AdminGroup.Length > 0 ? $" (admin group '{o.AdminGroup}')" : "")
            + $"; [oauth2_client] ACCOUNT_LINKING={o.AccountLinking}");
    }

    private static ApplyResult Combine(string? rootMsg, string msg) =>
        ApplyResult.Applied(rootMsg is null ? msg : $"{rootMsg}; {msg}");

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
            var bypass = ParseAccessBypass(s.Spec.Config);
            if (bypass.Count > 0)
                yield return $"reconcile the '{BypassPolicyName}' policy on each gated hostname to {string.Join(", ", bypass)}";
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
            // login still applies. Create-or-RECONCILE: unlike the allow policy this one's
            // CONTENT is declared by the shape, so existence-by-name isn't enough — the live
            // CIDR list is compared and rewritten on drift. Create-only was a silent trap:
            // the home network gained IPv6, browsers began reaching Cloudflare over it, and
            // the IPv4-only bypass stopped matching — while the shape looked correct and
            // every converge reported no change (#417).
            if (bypassIps.Count > 0)
            {
                var livePolicy = await api.GetAccessPolicyAsync(zone.AccountId, appId, BypassPolicyName, ct);
                if (livePolicy is null)
                {
                    await api.CreateAccessBypassIpPolicyAsync(zone.AccountId, appId, BypassPolicyName, bypassIps, ct);
                    gated++;
                }
                else if (BypassDrifted(livePolicy.IncludeIps, bypassIps))
                {
                    await api.UpdateAccessBypassIpPolicyAsync(zone.AccountId, appId, livePolicy.Id, BypassPolicyName, bypassIps, ct);
                    gated++;
                }
            }
        }
        // A bypass-only shape (Core: the Access apps are hand-made, we own just the policy)
        // still deserves a word in the no-change line — else it reads as if nothing is gated.
        var gateNote = allowEmails.Count > 0 ? $", {ingress.Count} host(s) Access-gated"
                     : bypassIps.Count > 0 ? ", trusted-IP bypass in sync" : "";

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

    // True when a live bypass policy's CIDR list differs from the shape's — the signal to
    // rewrite it. Set comparison, so order never matters; CIDRs are normalised first
    // because one IPv6 prefix has many spellings (`2001:DB8:116D:E500:0:0:0:0/56` and
    // `2001:db8:116d:e500::/56` are the same network) and Cloudflare echoes back its own.
    // Without that, an unchanged list would look drifted and be rewritten every converge.
    public static bool BypassDrifted(IEnumerable<string> live, IEnumerable<string> desired)
        => !NormalizeCidrs(live).SetEquals(NormalizeCidrs(desired));

    // Canonical form of a CIDR for comparison only — never for sending. Anything that
    // doesn't parse as an address is kept verbatim rather than dropped: an unparseable
    // entry must still count as a difference, not silently compare equal to nothing.
    private static HashSet<string> NormalizeCidrs(IEnumerable<string> cidrs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in cidrs)
        {
            var c = raw?.Trim();
            if (string.IsNullOrEmpty(c)) continue;
            var slash = c.IndexOf('/');
            var addr = slash < 0 ? c : c[..slash];
            var len = slash < 0 ? "" : c[slash..];
            set.Add(System.Net.IPAddress.TryParse(addr, out var ip) ? ip.ToString() + len : c);
        }
        return set;
    }

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
    //
    // ⚠ GERBIL AND PANGOLIN MUST BE BUMPED TOGETHER. Upstream publishes no compatibility
    // matrix — its installer Makefile injects whatever the LATEST gerbil tag is at build
    // time — so the pairing is only ever "the two that shipped around the same date", and
    // nothing in either project complains when they drift.
    //
    // Gerbil 1.1.0 (2025-08) against pangolin ee-1.19.4 (2026-06) broke every site
    // connector: gerbil adds the WireGuard peer locally, then registers it with Pangolin
    // WITHOUT a publicKey, which the newer API rejects —
    //     Server returned non-OK status: 400
    //     {"message":"Validation error: ... expected string, received undefined
    //                 at \"publicKey\"","status":400}
    // Pangolin never learns the peer exists, so newt waits on newt/wg/get-config forever.
    // Gerbil 1.3.0 is the release that added it ("Include public key in hole punch message
    // to Pangolin").
    //
    // The failure is SILENT until something forces re-registration — an existing peer keeps
    // working across the bump, so a stack update looks clean and the site only dies on its
    // next reconnect, potentially days later (#455).
    //
    // NEWT COUNTS AS PART OF THIS SET, even though it is pinned in the DevOps stack rather
    // than here (stacks/DevOps/newt.lxc.yaml). Holding pangolin at ee-1.19.4 while newt ran
    // 1.16.0 broke the other half: pangolin 1.21.0 added same-network detection for clients
    // and sites and says outright that it "requires updated clients and sites", while newt
    // 1.15.0 added the connector half ("scrape and send local endpoints to accept local
    // network connections from clients"). Running the client half against a server that has
    // neither left newt unable to build its clients interface — pangolin handed it a bare
    // `100.90.128.0` with no CIDR prefix (#457).
    internal const string DefaultImage = "fosrl/pangolin:ee-1.21.1";
    internal const string DefaultGerbilImage = "fosrl/gerbil:1.5.0";
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
        yield return "reconcile declared resources via the integration API (add-only by fullDomain; per-resource access rules reconciled where declared)";
        foreach (var idp in DeclaredIdps(s))
            yield return $"reconcile identity provider '{idp.Name}' + its claim→role mapping via the integration API";
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
            string.Join(",", AdditionalDomains(s)),
            // Hash the RENDERED ARTEFACTS, not just the field list above.
            //
            // The enumerated fields are a allowlist, and an allowlist is only correct until
            // someone adds a config key and forgets to list it here. That happened with
            // `includeGerbil`: `gerbilImage` was listed but the flag that decides whether
            // gerbil exists at all was not, so flipping it changed the desired compose —
            // gerbil added, traefik moved into its netns — while the marker stayed put and
            // converge reported NOCHANGE. Desired state moved and nothing was applied.
            //
            // Hashing the rendered compose + Traefik configs closes the whole class: any
            // input that changes what actually lands on the host changes the marker, whether
            // or not it is remembered above. Same lesson PodmanProvisioner records at its own
            // DesiredMarker, which hashes its generated script for exactly this reason.
            Sha(BuildComposeYaml(s)),
            Sha(BuildTraefikStatic(s, BaseDomain(s))),
            // ...and config.yml, which #437 missed. It carries gerbil.base_endpoint, whose
            // inputs (publicIp / gerbilEndpoint) are in NO other marker component — so changing
            // where connectors are told to send WireGuard rendered a new config.yml and still
            // reported NOCHANGE. "Hash the rendered artefacts" only works if it is ALL of them.
            // The marker placeholder keeps this deterministic: config.yml embeds the marker, so
            // hashing the real value would be circular.
            Sha(string.Join("\n", BuildConfigLines(s, "<marker>", DashboardHost(s), DashboardUrl(s), BaseDomain(s)))),
            // ...and the generated DEPLOY SCRIPT, which is the last hole in this marker.
            //
            // Hashing rendered artefacts catches a change to the artefacts. It does NOT catch a
            // change to the RECIPE — so fixing a bug in BuildDockerDeploy no-ops on every host
            // carrying the old marker, which is how the missing `docker compose restart` could
            // not be deployed: the fix was correct, the marker did not move, converge said
            // NOCHANGE. Third instance of this class in one sitting (#437, then config.yml, then
            // this), so hash the recipe as PodmanProvisioner has always done.
            //
            // The script embeds the base64 of compose/config/traefik, so this subsumes the
            // artefact hashes above; they are kept because a marker is cheap and a silent no-op
            // is not. Placeholders keep it deterministic.
            Sha(BuildDockerDeploy(s, "<marker>", DashboardHost(s), DashboardUrl(s), BaseDomain(s), "<cfToken>")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
    }

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    internal static string DashboardUrl(Shape s) => s.Spec.Config.Str("dashboardUrl") ?? "";

    internal static string DashboardHost(Shape s) =>
        s.Spec.Config.Str("dashboardUrl") is { Length: > 0 } u ? new Uri(u).Host : "";

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

    // Where a Newt connector is told to send WireGuard — rendered as gerbil.base_endpoint.
    //
    // This defaulted to the DASHBOARD host, which is correct only while Gerbil is idle. The
    // dashboard is served through the core Cloudflare tunnel, so `pangolin.chrison.dev`
    // resolves to Cloudflare (104.21.65.197 / 172.67.165.129, CLOUDFLARENET) — and Cloudflare's
    // proxy does not carry WireGuard UDP. A connector handed that endpoint sends handshakes to
    // Cloudflare's edge, which drops them, and the tunnel never comes up.
    //
    // In public-wildcard mode the address that actually reaches our edge is the home WAN IP —
    // the same `publicIp` the grey-cloud wildcard records already point at, and the same IP the
    // 51820/udp port-forward lives behind. An IP rather than a name is deliberate: it is static
    // (ADR-0007 open decision 5) and it keeps the WireGuard path from depending on any DNS.
    //
    // `gerbilEndpoint` overrides it — that is the knob the VPS graduation turns, when Gerbil
    // moves off-site and connectors dial the VPS instead.
    internal static string GerbilBaseEndpoint(Shape s, string dashboardHost)
    {
        var c = s.Spec.Config;
        if (c.Str("gerbilEndpoint") is { Length: > 0 } explicitEndpoint) return explicitEndpoint;
        if ((c.Str("edge") ?? "cloudflared") == PublicWildcard && c.Str("publicIp") is { Length: > 0 } ip)
            return ip;
        return dashboardHost;
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
        // WHAT ALREADY SUCCEEDED MUST SURVIVE A LATER FAILURE. Each step below can abort the
        // apply, and every one of those returns used to discard the messages from the steps
        // that had already run — so an apply that genuinely rewrote public routes and then
        // tripped on a later step reported the later step's error and nothing else. That is
        // not a cosmetic loss: another session read exactly that output, concluded the
        // resource reconcile had never run, and rebuilt a live resource by hand on the
        // strength of it. The output was wrong, not their reading of it.
        var done = new List<string>();
        ApplyResult FailWithContext(string reason) => ApplyResult.Failed(
            done.Count == 0 ? reason : $"{reason} — completed before this: {string.Join("; ", done)}");
        if (configMsg is not null) done.Add(configMsg);

        var (dnsMsg, dnsChanged, dnsFailed) = await ReconcileWildcardDnsAsync(s, ctx);
        if (dnsFailed is not null) return FailWithContext(dnsFailed);
        if (dnsMsg is not null) done.Add(dnsMsg);

        // Resources (declarative, #136): reconcile declared admin-UI resources via the
        // integration API (add-only, idempotent by fullDomain). Skipped — not failed —
        // until the org PANGOLIN_API_KEY exists, since that's a post-setup bootstrap secret.
        var (resMsg, resChanged, resFailed) = await ReconcileResourcesAsync(s, ctx, node, ctid);
        if (resFailed is not null) return FailWithContext(resFailed);
        if (resMsg is not null) done.Add(resMsg);

        // Identity providers (#468): register the homelab IdP as an OIDC provider for this
        // org, with the claim→role mapping. Same skipped-not-failed rule as resources.
        var (idpMsg, idpChanged, idpFailed) = await ReconcileIdpsAsync(s, ctx, node, ctid);
        if (idpFailed is not null) return FailWithContext(idpFailed);

        if (configMsg is null && !dnsChanged && !resChanged && !idpChanged)
            return ApplyResult.NoChange($"config current (marker {marker})"
                + (dnsMsg is null ? "" : $"; {dnsMsg}") + (resMsg is null ? "" : $"; {resMsg}")
                + (idpMsg is null ? "" : $"; {idpMsg}"));
        return ApplyResult.Applied(string.Join("; ", new[] { configMsg, dnsMsg, resMsg, idpMsg }.Where(x => x is not null)));
    }

    // ── Identity providers (#468) ─────────────────────────────────────────────────────
    // A declared IdP, flattened from config.idps[]. `ClientIdFrom`/`ClientSecretFrom` name
    // KEYS IN secrets.env rather than carrying values, so a shape never holds a credential
    // and the same pair can be handed to both ends of the OIDC relationship.
    internal readonly record struct DeclaredIdp(
        string Name, string AuthUrl, string TokenUrl, string ClientIdFrom, string ClientSecretFrom,
        string IdentifierPath, string EmailPath, string NamePath, string Scopes,
        bool AutoProvision, string? RoleMapping);

    internal static IReadOnlyList<DeclaredIdp> DeclaredIdps(Shape s)
    {
        var list = new List<DeclaredIdp>();
        if (!(s.Spec.Config.TryGetValue("idps", out var v) && v is IEnumerable<object> items)) return list;
        foreach (var it in items)
        {
            if (it is not System.Collections.IDictionary d) continue;
            string? Str(string k) => d[k]?.ToString() is { Length: > 0 } x ? x : null;
            if (Str("name") is not { } name || Str("authUrl") is not { } au || Str("tokenUrl") is not { } tu) continue;
            list.Add(new DeclaredIdp(
                name, au, tu,
                Str("clientIdFrom") ?? "", Str("clientSecretFrom") ?? "",
                Str("identifierPath") ?? "sub",
                Str("emailPath") ?? "email",
                Str("namePath") ?? "preferred_username",
                Str("scopes") ?? "openid profile email",
                !(d["autoProvision"] is { } ap && bool.TryParse(ap.ToString(), out var b) && !b),
                Str("roleMapping")));
        }
        return list;
    }

    // The create/update body for an OIDC IdP. Pulled out as a pure function so the JSON shape
    // is unit-testable without an API — the same reason ArrExec's dialects are.
    internal static string IdpOidcJson(DeclaredIdp idp, string clientId, string clientSecret)
    {
        var o = new JsonObject
        {
            ["name"] = idp.Name,
            ["clientId"] = clientId,
            ["clientSecret"] = clientSecret,
            ["authUrl"] = idp.AuthUrl,
            ["tokenUrl"] = idp.TokenUrl,
            ["identifierPath"] = idp.IdentifierPath,
            ["emailPath"] = idp.EmailPath,
            ["namePath"] = idp.NamePath,
            ["scopes"] = idp.Scopes,
            ["autoProvision"] = idp.AutoProvision,
            ["variant"] = "oidc",
        };
        return o.ToJsonString();
    }

    // Reconcile declared identity providers via the integration API.
    //
    // RECONCILES, never add-only. #309 is the precedent: the resource reconciler was add-only,
    // an edited target silently never reached Pangolin, and converge reported a clean plan
    // while the route was down. An IdP has the same failure shape and worse consequences —
    // an authUrl left pointing at an old issuer is a login loop nobody can debug from the
    // plan output, because the plan says everything matches.
    //
    // THE CLIENT SECRET IS NOT DRIFT-CHECKED, because it cannot be: the API never reads one
    // back. It is written on create and on any update we make for another reason. To rotate
    // it, change the value in Secrets Manager and edit something else on the IdP, or delete
    // the IdP and let this recreate it.
    //
    // Skipped — not failed — when the API key or the client credentials are absent, matching
    // the resource reconciler: a plan on a machine without the full secret set still converges
    // everything else rather than going red.
    private static async Task<(string? msg, bool changed, string? failed)> ReconcileIdpsAsync(
        Shape s, ConvergeContext ctx, string node, string ctid)
    {
        var idps = DeclaredIdps(s);
        if (idps.Count == 0) return (null, false, null);
        if (ctx.Secrets.Get("PANGOLIN_API_KEY") is not { Length: > 0 } key)
            return ("idps declared but PANGOLIN_API_KEY unset — skipped", false, null);
        if (s.Spec.Config.Str("org") is not { Length: > 0 } org)
            return (null, false, "idps declared but config.org (Pangolin org id) is missing");

        var ct = CancellationToken.None;
        // Prefer a ROOT-scoped key when one exists. IdPs are a SERVER-level object in
        // Pangolin, not an org-level one, so most of /idp/* refuses an org key outright
        // ("Key does not have root access", 403). PANGOLIN_API_KEY stays org-scoped for
        // resources — which is the narrower scope that work actually needs — and this reads
        // the root key only if it has been provisioned.
        var idpKey = ctx.Secrets.Get("PANGOLIN_ROOT_API_KEY") is { Length: > 0 } rk ? rk : key;
        var pg = new PangolinClient(ctx.Exec, node, ctid, idpKey);
        var (lok, lroot) = await pg.CallAsync("GET", "/idp", null, ct);
        if (!lok) return ("idps declared but the integration API would not list them — skipped", false, null);

        var live = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in DataArray(lroot, "idps"))
            if (e.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } ns)
                live[ns] = e;

        int created = 0, updated = 0, mapped = 0;
        // Collected rather than thrown. IDENTITY MUST NOT TAKE DOWN ROUTING: this runs in the
        // same apply as the resource reconcile, so a hard failure here means no public route
        // can be managed until the IdP is happy — two unrelated concerns welded together. The
        // precedent is three lines up, where an absent PANGOLIN_API_KEY is a note, not a fault.
        var notes = new List<string>();
        foreach (var idp in idps)
        {
            var clientId = ctx.Secrets.Get(idp.ClientIdFrom);
            var clientSecret = ctx.Secrets.Get(idp.ClientSecretFrom);
            if (clientId is not { Length: > 0 } || clientSecret is not { Length: > 0 })
                return ($"idp '{idp.Name}' declared but {idp.ClientIdFrom}/{idp.ClientSecretFrom} unset — skipped", false, null);

            var body = IdpOidcJson(idp, clientId, clientSecret);
            int idpId;
            if (!live.TryGetValue(idp.Name, out var cur))
            {
                var (cok, croot) = await pg.CallAsync("PUT", "/idp/oidc", body, ct);
                if (!cok || !Data(croot).TryGetProperty("idpId", out var nid))
                {
                    notes.Add($"idp '{idp.Name}' could not be created (root-scoped endpoint?) — skipped");
                    continue;
                }
                idpId = nid.GetInt32();
                created++;
            }
            else
            {
                if (!cur.TryGetProperty("idpId", out var cid))
                {
                    notes.Add($"idp '{idp.Name}' has no idpId in the listing — skipped");
                    continue;
                }
                idpId = cid.GetInt32();

                // ⚠ THE LISTING IS NOT ENOUGH TO COMPARE AGAINST, and assuming it was is what
                // broke this. GET /idp returns only {idpId, name, type, variant, orgCount,
                // autoProvision, tags} — none of authUrl, tokenUrl, scopes or the claim paths.
                // Diffing the declared values against a payload that never contained them made
                // IdpDrifted true on EVERY run, which fired an update, which 403s on an
                // org-scoped key, which failed the whole apply — taking resource management
                // down with it on a stack whose IdP was already correct.
                //
                // The per-IdP detail endpoint has the fields but is root-only, so with an org
                // key drift is genuinely unknowable. Say so in the plan output rather than
                // guessing in either direction.
                var (dok, droot) = await pg.CallAsync("GET", $"/idp/{idpId}", null, ct);
                if (!dok)
                {
                    notes.Add($"idp '{idp.Name}' present; config drift NOT checked (GET /idp/{idpId} needs a root-scoped key — set PANGOLIN_ROOT_API_KEY)");
                }
                else if (IdpDrifted(Data(droot), idp))
                {
                    var (uok, _) = await pg.CallAsync("POST", $"/idp/{idpId}/oidc", body, ct);
                    if (!uok) notes.Add($"idp '{idp.Name}' has drifted but could not be updated (needs a root-scoped key) — left as-is");
                    else updated++;
                }
            }

            // Org policy: the claim→role mapping. PUT creates it; if it already exists PUT is
            // rejected, so fall back to POST. Cheaper and more reliable than a read-then-branch
            // against an endpoint that has no documented GET.
            if (idp.RoleMapping is { Length: > 0 } rm)
            {
                var mapBody = new JsonObject { ["roleMapping"] = rm }.ToJsonString();
                var (pok, _) = await pg.CallAsync("PUT", $"/idp/{idpId}/org/{org}", mapBody, ct);
                if (!pok)
                {
                    var (pok2, _) = await pg.CallAsync("POST", $"/idp/{idpId}/org/{org}", mapBody, ct);
                    if (!pok2) { notes.Add($"idp '{idp.Name}' role mapping could not be set — skipped"); continue; }
                }
                mapped++;
            }
        }

        var changed = created > 0 || updated > 0;
        var msg = $"{idps.Count} idp(s) declared, {created} created, {updated} updated, {mapped} role mapping(s) applied";
        if (notes.Count > 0) msg += "; " + string.Join("; ", notes);
        return (msg, changed, null);
    }

    // Field-by-field drift on the readable parts of an IdP. The client secret is absent by
    // design (see above) and is therefore not part of the comparison.
    internal static bool IdpDrifted(JsonElement live, DeclaredIdp want)
    {
        static string S(JsonElement e, string p) =>
            e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return S(live, "authUrl") != want.AuthUrl
            || S(live, "tokenUrl") != want.TokenUrl
            || S(live, "identifierPath") != want.IdentifierPath
            || S(live, "emailPath") != want.EmailPath
            || S(live, "namePath") != want.NamePath
            || S(live, "scopes") != want.Scopes
            || Truthy(live, "autoProvision") != want.AutoProvision;
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
        // ...plus every site BY NAME, so a resource can bind to one explicitly with `site:`.
        // That is the thin slice of multi-site support the SSH path needs (#440): a Pangolin
        // SSH resource works only on a NEWT site, never on the local one, so it cannot ride
        // the single local site every HTTP resource uses. The full per-stack model is #442.
        int? siteId = null;
        var sitesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var (sok, sroot) = await pg.CallAsync("GET", $"/org/{org}/sites", null, ct);
        if (sok)
            foreach (var st in DataArray(sroot, "sites"))
            {
                if (!st.TryGetProperty("siteId", out var si)) continue;
                if (st.TryGetProperty("name", out var sn) && sn.GetString() is { Length: > 0 } sns)
                    sitesByName[sns] = si.GetInt32();
                if (st.TryGetProperty("type", out var t) && t.GetString() == "local")
                    siteId = si.GetInt32();
            }
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

        int total = 0, created = 0, retargeted = 0, regated = 0, ruled = 0;
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
            // `mode` selects the resource type. Default http keeps every existing entry as-is;
            // `ssh` is the protocol-aware SSH resource, which serves a BROWSER TERMINAL behind
            // the Pangolin auth layer (as opposed to a raw TCP resource, which bypasses auth
            // entirely). Pangolin defaults its pamMode/authDaemonMode/authDaemonPort.
            var mode = rd["mode"]?.ToString() is { Length: > 0 } md ? md : "http";
            var isSsh = string.Equals(mode, "ssh", StringComparison.OrdinalIgnoreCase);

            var tgt = rd["target"] as System.Collections.IDictionary;
            var tip = tgt?["ip"]?.ToString() ?? "localhost";
            // An SSH target carries no method — Pangolin stores null, and sending "http" makes
            // the comparison below drift forever.
            var tmethod = isSsh ? null : (tgt?["method"]?.ToString() ?? "http");
            var tport = int.TryParse(tgt?["port"]?.ToString(), out var pp) ? pp : (isSsh ? 22 : 80);

            // Which site the target hangs off. Omitted → the local site, so nothing existing
            // moves. Named → resolved from the live list; an unknown name fails THAT resource
            // loudly rather than silently planting it on the local site, which for an SSH
            // resource would produce a route that can never work.
            var rSiteId = siteId;
            if (rd["site"]?.ToString() is { Length: > 0 } wantSite)
            {
                if (!sitesByName.TryGetValue(wantSite, out var found))
                {
                    notes.Add($"{sub}: site '{wantSite}' not found in Pangolin — declare its connector first (see #441)");
                    continue;
                }
                rSiteId = found;
            }
            else if (isSsh)
            {
                notes.Add($"{sub}: mode ssh requires an explicit `site:` — SSH resources work only on a Newt site, never the local one");
                continue;
            }
            // sso gate: default ON — admin UIs must sit behind Pangolin auth (badger). The
            // integration-API create defaults sso to null (OPEN), so we MUST set it explicitly
            // or the resource is born publicly reachable. A resource may opt out (sso: false)
            // for native clients that can't render the SSO interstitial (e.g. Plex, abs).
            var sso = ResourceSsoEnabled(rd);

            if (existing.TryGetValue(fqdn, out var live))
            {
                // ── EXISTS: reconcile rather than skip (#309) ──────────────────────────
                var (tmsg, tchanged, tfail) = await ReconcileTargetAsync(
                    pg, live, rSiteId!.Value, tip, tmethod, tport, fqdn, ct);
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

                var (rnotes, rchanged, rfail) = await ReconcileRulesAsync(pg, live.Id, rd, fqdn, ct);
                if (rfail is not null) return (null, false, rfail);
                notes.AddRange(rnotes);
                if (rchanged) ruled++;
                continue;
            }

            // ── ABSENT: create ────────────────────────────────────────────────────────
            var createBody = isSsh
                // An SSH resource is created by MODE. It carries no `http`/`protocol` — sending
                // them makes Pangolin treat it as an HTTP resource and the terminal never appears.
                ? JsonSerializer.Serialize(new { name, subdomain = pgSub, mode = "ssh", domainId })
                : JsonSerializer.Serialize(new { name, subdomain = pgSub, http = true, protocol = "tcp", domainId });
            var (rok, rroot) = await pg.CallAsync("PUT", $"/org/{org}/resource", createBody, ct);
            if (!rok || !Data(rroot).TryGetProperty("resourceId", out var rid))
                return (null, false, $"pangolin: failed to create resource {fqdn}");
            var resourceId = rid.GetInt32();
            await pg.CallAsync("PUT", $"/resource/{resourceId}/target",
                JsonSerializer.Serialize(new { siteId = rSiteId, ip = tip, method = tmethod, port = tport, enabled = true }), ct);
            // ssl: public-wildcard → Traefik terminates TLS (true); cloudflared → CF does (false).
            // sso: gate the resource behind Pangolin auth unless it explicitly opts out.
            await pg.CallAsync("POST", $"/resource/{resourceId}", JsonSerializer.Serialize(new { ssl = publicWildcard, sso }), ct);
            var (cnotes, cchanged, cfail) = await ReconcileRulesAsync(pg, resourceId, rd, fqdn, ct);
            if (cfail is not null) return (null, false, cfail);
            notes.AddRange(cnotes);
            if (cchanged) ruled++;
            created++;
        }

        var summary = $"{total} resource(s) declared, {created} created, {retargeted} retargeted, {regated} re-gated, {ruled} rule set(s) changed";
        if (notes.Count > 0) summary += "\n      " + string.Join("\n      ", notes);
        return (summary, created + retargeted + regated + ruled > 0, null);
    }

    // Reconcile one resource's ACCESS RULES — the per-path / per-IP layer Pangolin evaluates
    // BEFORE the sso/pincode/password gates (badger verifySession → checkRules, when the
    // resource's applyRules is on). This is how a native client that cannot render the SSO
    // interstitial — Ruddarr talking to Sonarr/Radarr with an X-Api-Key header — gets through
    // on `/api/*` while the UI on the SAME hostname stays behind SSO. Declared per resource:
    //
    //   rules:
    //     - { action: ACCEPT, match: PATH, value: "/api/*" }
    //
    // `action` is Pangolin's own enum, kept verbatim so the shape and the UI never disagree:
    // ACCEPT = BYPASS auth (let it through), DROP = block outright, PASS = fall through to the
    // normal auth gates. `priority` defaults to the list position (ascending, first match
    // wins); `enabled` defaults true. A `*` segment matches whole segments at any depth, so
    // "/api/*" covers /api/v3/series/12 — but matching is segment-based, so "/api*" would NOT.
    //
    // Keyed by (match, value): declared-but-absent → create; present-but-different
    // (action/priority/enabled) → update; LIVE RULES NOT DECLARED HERE ARE LEFT ALONE and
    // reported — the same add-only stance the resource list takes. applyRules (without which
    // rules are inert) is switched on only for a resource that declares rules, and only after
    // they exist. A resource declaring no rules costs no extra calls and is not touched, so the
    // dozen SSO-only entries behave exactly as before.
    private static async Task<(List<string> notes, bool changed, string? failed)> ReconcileRulesAsync(
        PangolinClient pg, int resourceId, System.Collections.IDictionary rd, string fqdn, CancellationToken ct)
    {
        var notes = new List<string>();
        if (rd["rules"] is not IEnumerable<object> declared) return (notes, false, null);

        var wanted = new List<(string Action, string Match, string Value, int Priority, bool Enabled)>();
        var i = 0;
        foreach (var o in declared)
        {
            i++;
            if (o is not System.Collections.IDictionary r) continue;
            var action = r["action"]?.ToString()?.ToUpperInvariant() ?? "";
            var match = r["match"]?.ToString()?.ToUpperInvariant() ?? "";
            var value = r["value"]?.ToString() ?? "";
            // A malformed rule is a security-relevant config error — fail the apply rather than
            // silently skipping it and leaving the resource in an undeclared state.
            if (action is not ("ACCEPT" or "DROP" or "PASS"))
                return (notes, false, $"pangolin: {fqdn} rule {i}: action '{action}' is not ACCEPT|DROP|PASS");
            if (match.Length == 0 || value.Length == 0)
                return (notes, false, $"pangolin: {fqdn} rule {i}: match and value are required");
            var priority = int.TryParse(r["priority"]?.ToString(), out var pr) ? pr : i;
            var enabled = !(r["enabled"] is { } en && bool.TryParse(en.ToString(), out var eb) && !eb);
            wanted.Add((action, match, value, priority, enabled));
        }
        if (wanted.Count == 0) return (notes, false, null);

        var (lok, lroot) = await pg.CallAsync("GET", $"/resource/{resourceId}/rules", null, ct);
        if (!lok) return (notes, false, $"pangolin: GET rules failed for {fqdn} — cannot reconcile safely");
        var live = new Dictionary<(string Match, string Value), (int Id, string Action, int Priority, bool Enabled)>();
        foreach (var lr in DataArray(lroot, "rules"))
        {
            if (!lr.TryGetProperty("ruleId", out var rid)) continue;
            var m = lr.TryGetProperty("match", out var mv) ? (mv.GetString() ?? "").ToUpperInvariant() : "";
            var v = lr.TryGetProperty("value", out var vv) ? vv.GetString() ?? "" : "";
            var a = lr.TryGetProperty("action", out var av) ? (av.GetString() ?? "").ToUpperInvariant() : "";
            var p = lr.TryGetProperty("priority", out var pv) && pv.TryGetInt32(out var pi) ? pi : 0;
            live[(m, v)] = (rid.GetInt32(), a, p, Truthy(lr, "enabled"));
        }

        var changed = false;
        foreach (var w in wanted)
        {
            var body = JsonSerializer.Serialize(new { action = w.Action, match = w.Match, value = w.Value, priority = w.Priority, enabled = w.Enabled });
            if (live.Remove((w.Match, w.Value), out var l))
            {
                if (l.Action == w.Action && l.Priority == w.Priority && l.Enabled == w.Enabled) continue;
                var (uok, _) = await pg.CallAsync("POST", $"/resource/{resourceId}/rule/{l.Id}", body, ct);
                if (!uok) return (notes, false, $"pangolin: failed to update rule {w.Match} {w.Value} on {fqdn}");
                notes.Add($"{fqdn}: rule {w.Match} {w.Value}: {l.Action}/p{l.Priority}/{(l.Enabled ? "on" : "off")} → {w.Action}/p{w.Priority}/{(w.Enabled ? "on" : "off")}");
            }
            else
            {
                var (cok, _) = await pg.CallAsync("PUT", $"/resource/{resourceId}/rule", body, ct);
                if (!cok) return (notes, false, $"pangolin: failed to create rule {w.Match} {w.Value} on {fqdn}");
                notes.Add($"{fqdn}: rule added {w.Action} {w.Match} {w.Value}");
            }
            changed = true;
        }
        foreach (var (k, l) in live)
            notes.Add($"{fqdn}: undeclared live rule {l.Action} {k.Match} {k.Value} — left alone, delete by hand if unwanted");

        // applyRules gates the whole layer and the embedded resource list omits it, so it costs
        // one detail call — only for resources that declare rules.
        var (dok, droot) = await pg.CallAsync("GET", $"/resource/{resourceId}", null, ct);
        if (!dok) return (notes, false, $"pangolin: GET resource failed for {fqdn}");
        if (!Truthy(Data(droot), "applyRules"))
        {
            var (aok, _) = await pg.CallAsync("POST", $"/resource/{resourceId}", JsonSerializer.Serialize(new { applyRules = true }), ct);
            if (!aok) return (notes, false, $"pangolin: failed to enable applyRules on {fqdn}");
            notes.Add($"{fqdn}: rules enabled (applyRules on)");
            changed = true;
        }
        return (notes, changed, null);
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
        string ip, string? method, int port, string fqdn, CancellationToken ct)
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

        // method is null for an SSH target and Pangolin stores it as null, so compare against
        // "" rather than letting a null-vs-"" mismatch look like permanent drift.
        if (string.Equals(liveIp, ip, StringComparison.OrdinalIgnoreCase) && livePort == port
            && string.Equals(liveMethod, method ?? "", StringComparison.OrdinalIgnoreCase) && liveEnabled)
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
            $"    base_endpoint: \"{GerbilBaseEndpoint(s, host)}\"",
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
        // ...then RESTART, because `up -d` is not enough. It only recreates a service whose
        // DEFINITION changed, and config.yml / traefik_config.yml are bind-mounted files — so a
        // config-only change (a new gerbil.base_endpoint, say) lands on disk while the running
        // process keeps serving the old value from memory, and converge reports APPLIED.
        // Observed exactly that: base_endpoint rendered to 10.10.0.13 while every container
        // still showed `Up 3 days` and Newt was handed the previous endpoint. The native
        // (non-Docker) path has always done `systemctl restart pangolin gerbil` for this reason;
        // the Docker path was missing its equivalent.
        //
        // Blunt on purpose — all three read rendered config, and the whole deploy is
        // marker-gated, so this only runs when something actually changed.
        sb.Append("docker compose restart\n");
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
            $"    base_endpoint: \"{GerbilBaseEndpoint(s, host)}\"",
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
                // reachableAt MUST match where gerbil actually listens. This advertised :3004
                // while gerbil's -listen defaults to ":3003", so Pangolin's peer add/delete
                // calls hit a closed port: "Error making POST request (can Pangolin see Gerbil
                // HTTP API?) ... connect ECONNREFUSED 172.18.0.3:3004". Newt then waits forever
                // on newt/wg/get-config, never receives a tunnel config, and every ping fails —
                // while the site still reports online, because the websocket control plane is
                // fine. Latent since gerbil support was written; only reachable once gerbil was
                // actually switched on (#432).
                "      - --reachableAt=http://gerbil:3003",
                "      - --generateAndSaveKeyTo=/var/config/key",
                // remoteConfig supersedes reportBandwidthTo, which gerbil marks DEPRECATED in
                // its own --help. Passing both is redundant, so pass the one that is current.
                "      - --remoteConfig=http://pangolin:3001/api/v1/gerbil/get-config",
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
