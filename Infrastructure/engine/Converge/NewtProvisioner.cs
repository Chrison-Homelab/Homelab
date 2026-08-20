using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Newt site connector (#441) — the WireGuard connector that gives a stack its own Pangolin
// site, so the stack's exposure travels with the stack (the per-stack model, #442).
//
// CREATE half: `app: debian` → ct/debian.sh, a plain base CT.
//
// Why a binary and not a container: Pangolin SSH resources require Newt running as root on
// the connector host, and upstream states plainly that containerized installations are
// UNSUPPORTED for SSH mode. That is a deliberate, narrow exception to ADR-0009's
// podman/quadlet direction — not an oversight, and not something to "fix" into a quadlet.
//
// CREDENTIALS. Pangolin issues a site's newtId + secret exactly ONCE, in the response to the
// site-create call. The integration API has no endpoint to read them back or regenerate them
// (probed: /site/{id}/credentials, /regenerate, /rotate-credentials all 404). So this
// provisioner resolves credentials in strict order:
//
//   1. NEWT_ID + NEWT_SECRET in secrets.env  → use them. This is the rebuild-safe path, and
//      the one to use after regenerating credentials by hand in the dashboard.
//   2. The site does not exist yet           → create it, capture the credentials from the
//      create response, and write them to the connector's env file.
//   3. The site exists but the connector has no credentials (e.g. the CT was rebuilt) →
//      FAIL with instructions. It must not silently delete and recreate the site: a site
//      owns its resources, so recreating it would take them with it.
public sealed class NewtProvisioner : IAppProvisioner
{
    public string App => "newt";

    // Pinned. `newt_linux_amd64` from fosrl/newt releases.
    internal const string DefaultVersion = "1.16.0";

    internal const string BinPath = "/usr/local/bin/newt";
    internal const string EnvDir = "/etc/newt";
    internal const string EnvPath = "/etc/newt/newt.env";
    // Where Newt persists resolved credentials. Pinned under /etc rather than left to the
    // platform default (~/.config/newt-client/config.json), so a root daemon's state is
    // somewhere a human would look.
    internal const string ConfigPath = "/etc/newt/config.json";
    internal const string UnitPath = "/etc/systemd/system/newt.service";
    internal const string UnitName = "newt.service";

    // curl to fetch the release; ca-certificates so TLS to GitHub works on a bare CT.
    internal static readonly string[] BasePackages = { "curl", "ca-certificates" };

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var site = SiteName(s);
        yield return $"install {string.Join(", ", BasePackages)}";
        yield return $"fetch newt {Version(s)} (newt_linux_amd64) → {BinPath}";
        yield return $"resolve credentials for Pangolin site '{site}': secrets.env NEWT_ID/NEWT_SECRET, " +
                     "else create the site via the integration API and capture them from the response";
        yield return $"write {EnvPath} (0600 — it holds the site secret) + {UnitPath}";
        yield return $"enable {UnitName} as root — Pangolin SSH mode requires the binary, not a container";
        yield return $"connector dials {Endpoint(s)}; WireGuard goes to gerbil.base_endpoint";
    }

    // The marker deliberately EXCLUDES the credentials. They are not a desired-state input —
    // they are issued once and then fixed — and hashing them would make the marker depend on a
    // value we cannot re-read, so a converge that could not resolve them would look like drift.
    public static string DesiredMarker(Shape s) =>
        Sha(string.Join('|',
            Version(s),
            SiteName(s),
            Endpoint(s),
            string.Join(",", Packages(s)),
            Sha(BuildUnit(s))))[..12];

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.Failed("missing node/ctid");

        var marker = DesiredMarker(s);
        var markerPath = "/etc/newt/.homelab-managed";

        var cur = await ctx.Exec.InContainerAsync(node, ctid, $"cat {markerPath} 2>/dev/null || true");
        var markerCurrent = cur.Stdout.Trim() == marker;

        // Credentials live on the CT, outside the marker — so a current marker is only
        // "nothing to do" if the env file is actually there. Without this check a CT rebuilt
        // under an unchanged shape would report NoChange with no connector configured.
        var haveEnv = (await ctx.Exec.InContainerAsync(node, ctid,
            $"test -s {EnvPath} && echo yes || true")).Stdout.Trim() == "yes";

        if (markerCurrent && haveEnv)
            return ApplyResult.NoChange($"newt connector current for site '{SiteName(s)}' (marker {marker})");

        var (creds, credsFailed) = await ResolveCredentialsAsync(s, ctx, node, ctid, haveEnv);
        if (credsFailed is not null) return ApplyResult.Failed(credsFailed);

        ctx.Report($"installing newt {Version(s)} and registering site '{SiteName(s)}'");

        var res = await ctx.Exec.InContainerAsync(node, ctid, BuildDeploy(s, creds, marker, markerPath));
        if (!res.Ok) return ApplyResult.Failed($"newt setup failed: {res.Stderr}");

        var how = creds is null ? "existing credentials preserved" : $"site '{SiteName(s)}' registered (newtId {creds.Value.Id})";
        return ApplyResult.Applied($"newt {Version(s)} installed + enabled; {how} (marker {marker})");
    }

    // ── credentials ─────────────────────────────────────────────────────────────────
    //
    // Returns null credentials to mean "leave whatever the env file already has alone".
    private static async Task<((string Id, string Secret)? Creds, string? Failed)> ResolveCredentialsAsync(
        Shape s, ConvergeContext ctx, string node, string ctid, bool haveEnv)
    {
        // 1. Explicit, from secrets.env. Wins over everything — this is how a rebuilt
        //    connector or a dashboard-regenerated credential pair gets adopted.
        var envId = ctx.Secrets.Get("NEWT_ID");
        var envSecret = ctx.Secrets.Get("NEWT_SECRET");
        if (envId is { Length: > 0 } && envSecret is { Length: > 0 })
            return ((envId, envSecret), null);

        if (ctx.Secrets.Get("PANGOLIN_API_KEY") is not { Length: > 0 } apiKey)
            return (null, "newt needs PANGOLIN_API_KEY in secrets.env to create its Pangolin site " +
                          "(or NEWT_ID + NEWT_SECRET to adopt an existing one)");

        var pangolinCtid = PangolinCtid(s);
        var pangolinNode = PangolinNode(s);
        var org = Org(s);
        var siteName = SiteName(s);

        // Is the site already there? Matched by name — niceId is server-assigned and random
        // ("needy-western-mouse"), so it cannot be the key.
        var list = await ApiAsync(ctx, pangolinNode, pangolinCtid, apiKey, "GET", $"/org/{org}/sites", null);
        if (list is null) return (null, $"newt: could not list sites on the Pangolin CT ({pangolinNode}/{pangolinCtid})");

        var existing = FindSite(list, siteName);
        if (existing is not null)
        {
            // 3. Site exists. If the connector already carries credentials, this is a re-run
            //    for some other reason (a version bump, say) — keep them.
            if (haveEnv) return (null, null);

            return (null,
                $"newt: Pangolin site '{siteName}' already exists (siteId {existing}) but this connector has no " +
                $"credentials, and Pangolin issues them only once at create — there is no API to read or " +
                $"regenerate them. Either regenerate them in the dashboard (Sites → {siteName} → Credentials) " +
                $"and set NEWT_ID + NEWT_SECRET in secrets.env, or delete the site so converge recreates it. " +
                $"NOT deleting it automatically: a site owns its resources and would take them with it.");
        }

        // 2. Create it, and capture the one-time credentials.
        var body = JsonSerializer.Serialize(new { name = siteName, type = "newt" });
        var created = await ApiAsync(ctx, pangolinNode, pangolinCtid, apiKey, "PUT", $"/org/{org}/site", body);
        if (created is null) return (null, $"newt: failed to create Pangolin site '{siteName}'");

        if (!created.RootElement.TryGetProperty("data", out var d)
            || !d.TryGetProperty("newtId", out var nid) || !d.TryGetProperty("secret", out var sec))
            return (null, $"newt: site '{siteName}' was created but the response carried no newtId/secret — " +
                          "cannot configure the connector, and the credentials are not retrievable afterwards");

        return ((nid.GetString() ?? "", sec.GetString() ?? ""), null);
    }

    private static int? FindSite(JsonDocument sites, string name)
    {
        if (!sites.RootElement.TryGetProperty("data", out var d) || !d.TryGetProperty("sites", out var arr))
            return null;
        foreach (var site in arr.EnumerateArray())
            if (site.TryGetProperty("name", out var n) && n.GetString() == name
                && site.TryGetProperty("siteId", out var id))
                return id.GetInt32();
        return null;
    }

    // The integration API is bound to 127.0.0.1 INSIDE the Pangolin CT, so every call is a
    // curl executed in that CT — the same route PangolinProvisioner takes.
    private static async Task<JsonDocument?> ApiAsync(
        ConvergeContext ctx, string node, string ctid, string key, string method, string path, string? body)
    {
        var cmd = new StringBuilder($"curl -s -X {method} -H 'Authorization: Bearer {key}'");
        if (body is not null)
            cmd.Append($" -H 'Content-Type: application/json' -d '{body.Replace("'", "'\\''")}'");
        cmd.Append($" http://127.0.0.1:3003/v1{path}");

        var res = await ctx.Exec.InContainerAsync(node, ctid, cmd.ToString());
        if (!res.Ok || string.IsNullOrWhiteSpace(res.Stdout)) return null;
        try { return JsonDocument.Parse(res.Stdout); } catch (JsonException) { return null; }
    }

    // ── the deploy script ───────────────────────────────────────────────────────────

    internal static string BuildDeploy(Shape s, (string Id, string Secret)? creds, string marker, string markerPath)
    {
        var sb = new StringBuilder();
        sb.Append("set -e\n");
        sb.Append("export DEBIAN_FRONTEND=noninteractive\n");
        sb.Append("apt-get update -qq\n");
        sb.Append($"apt-get install -y -qq --no-install-recommends {string.Join(' ', Packages(s))}\n");

        sb.Append($"install -d -m 700 {EnvDir}\n");

        // Fetch to a temp path and move into place, so a half-downloaded binary never becomes
        // /usr/local/bin/newt. -f so an HTTP error is a failure rather than an HTML "binary".
        var url = $"https://github.com/fosrl/newt/releases/download/{Version(s)}/newt_linux_amd64";
        sb.Append($"curl -fsSL -o {BinPath}.new {url}\n");
        sb.Append($"chmod 0755 {BinPath}.new\n");
        sb.Append($"mv {BinPath}.new {BinPath}\n");

        // Credentials, only when we have a pair to write. When creds is null the existing env
        // file is authoritative and must not be clobbered — Pangolin will not reissue it.
        if (creds is { } c)
        {
            sb.Append($"cat > {EnvPath} <<'HOMELAB_NEWT_ENV'\n");
            sb.Append("# MANAGED BY converge (NewtProvisioner) — holds this site's secret.\n");
            sb.Append($"NEWT_ID={c.Id}\n");
            sb.Append($"NEWT_SECRET={c.Secret}\n");
            sb.Append($"PANGOLIN_ENDPOINT={Endpoint(s)}\n");
            sb.Append($"CONFIG_FILE={ConfigPath}\n");
            sb.Append("HOMELAB_NEWT_ENV\n");
            sb.Append($"chmod 0600 {EnvPath}\n");
        }

        sb.Append($"cat > {UnitPath} <<'HOMELAB_NEWT_UNIT'\n");
        sb.Append(BuildUnit(s));
        sb.Append("HOMELAB_NEWT_UNIT\n");
        sb.Append("systemctl daemon-reload\n");
        sb.Append($"systemctl enable {UnitName}\n");
        // restart, not start: a version bump has to replace a running connector.
        sb.Append($"systemctl restart {UnitName}\n");

        sb.Append($"printf '%s' '{marker}' > {markerPath}\n");
        return sb.ToString();
    }

    internal static string BuildUnit(Shape s) => $"""
        [Unit]
        Description=Newt site connector for Pangolin site '{SiteName(s)}'
        Documentation=https://github.com/Chrison-Homelab/Homelab/issues/441
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=simple
        # Root is a REQUIREMENT, not laziness: Pangolin SSH mode needs Newt running as root on
        # the connector host, and does not support the containerized form at all.
        User=root
        Group=root
        EnvironmentFile={EnvPath}
        ExecStart={BinPath}
        Restart=always
        RestartSec=5

        [Install]
        WantedBy=multi-user.target

        """;

    // ── config accessors ────────────────────────────────────────────────────────────

    internal static string Version(Shape s) => s.Spec.Config.Str("version") ?? DefaultVersion;

    // The Pangolin site name. Defaults to the stack, which is the whole point of the
    // per-stack model (#442) — a site per stack, named after it.
    internal static string SiteName(Shape s) =>
        s.Spec.Config.Str("siteName") ?? s.Metadata.Stack ?? "homelab";

    internal static string Endpoint(Shape s) =>
        s.Spec.Config.Str("pangolinUrl") ?? "https://pangolin.chrison.dev";

    internal static string Org(Shape s) => s.Spec.Config.Str("org") ?? "chrison-dev";

    // Which CT hosts Pangolin — the integration API only listens on its localhost.
    internal static string PangolinCtid(Shape s) => s.Spec.Config.Str("pangolinCtid") ?? "2013";
    internal static string PangolinNode(Shape s) => s.Spec.Config.Str("pangolinNode") ?? "nuc-01";

    internal static IReadOnlyList<string> Packages(Shape s) =>
        BasePackages.Concat(StringList(s, "packages"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> StringList(Shape s, string key) =>
        s.Spec.Config.TryGetValue(key, out var v) && v is IEnumerable<object> e
            ? e.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList()
            : Array.Empty<string>();

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
