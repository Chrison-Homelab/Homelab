using System.Text.Json;
using System.Text.Json.Nodes;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// arr-wire config-migration provisioners (issue #159, plan docs/plans/159-arr-wire.md).
// Self-contained new→new wiring of the Media stack: each is idempotent (read the
// app's REST API, change only what's missing). THIS pass is the core spine —
// qBittorrent + Prowlarr + Sonarr + Radarr. Bazarr/Seerr/Recyclarr deferred.

// Shared helpers for talking to an *arr CT over the LAN.
public static class ArrExec
{
    // First IPv4 of the CT (the apps bind all interfaces; we reach them by LAN IP).
    public static async Task<string?> CtIpAsync(ConvergeContext ctx, string node, string ctid, CancellationToken ct)
    {
        var r = await ctx.Exec.InContainerAsync(node, ctid, "hostname -I | awk '{print $1}'", ct);
        return r.Ok && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
    }

    // sonarr/radarr/prowlarr store their key in config.xml. Find it wherever the
    // community-scripts install put it, then pull <ApiKey>.
    public static async Task<string?> ApiKeyAsync(ConvergeContext ctx, string node, string ctid, CancellationToken ct)
    {
        var r = await ctx.Exec.InContainerAsync(node, ctid,
            "cat $(find /var/lib /opt /root /config -name config.xml 2>/dev/null | head -1) 2>/dev/null", ct);
        return r.Ok ? ParseApiKey(r.Stdout) : null;
    }

    public static string? ParseApiKey(string configXml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(configXml, "<ApiKey>([0-9a-fA-F]+)</ApiKey>");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Resolve a sibling Media member (by shape name) to its CT IP — node/ctid via
    // ConvergeContext.ByName, IP read live over Exec.
    public static async Task<string?> SiblingIpAsync(ConvergeContext ctx, string name, CancellationToken ct)
    {
        if (!ctx.ByName.TryGetValue(name, out var dep) || dep.Spec.Node is not { } n || dep.Spec.Ctid is not { } c)
            return null;
        return await CtIpAsync(ctx, n, c.ToString(), ct);
    }

    // True if a Servarr resource array already has an element with this name (case-insensitive).
    public static bool HasName(JsonElement arr, string name) =>
        arr.ValueKind == JsonValueKind.Array && arr.EnumerateArray().Any(e =>
            e.TryGetProperty("name", out var n) && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase));

    // qBittorrent (4.6+) prints a random WebUI password to its journal on first run:
    //   "...A temporary password is provided for this session: <pw>".
    public static string? ParseQbitTempPassword(string journal)
    {
        string? pw = null;
        foreach (var line in journal.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, "temporary password.*session:\\s*(\\S+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) pw = m.Groups[1].Value;   // last one wins (most recent restart)
        }
        return pw;
    }
}

// qBittorrent (CT 5104) — WebUI creds + the two *arr categories on the shared /data
// export. Runs before sonarr/radarr (they add it as a download client).
public sealed class QbittorrentProvisioner : IAppProvisioner
{
    public string App => "qbittorrent";

    // category → save path under the shared /data export (torrents/ and media/ are
    // siblings so *arr hardlink-moves on import).
    private static readonly (string Name, string Path)[] Categories =
    {
        ("tv-sonarr", "/data/torrents/tv"),
        ("radarr",    "/data/torrents/movies"),
    };

    public IEnumerable<string> PlanSteps(Shape s)
    {
        yield return "ensure WebUI creds (QBIT_USER/QBIT_PASSWORD) + default save path /data/torrents";
        yield return "ensure categories: " + string.Join(", ", Categories.Select(c => $"{c.Name} → {c.Path}"));
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        var user = ctx.Secrets.Get("QBIT_USER") ?? "admin";
        var pass = ctx.Secrets.Get("QBIT_PASSWORD");
        if (string.IsNullOrEmpty(pass)) return ApplyResult.Failed("QBIT_PASSWORD not set in secrets.env");

        var ip = await ArrExec.CtIpAsync(ctx, node, ctid.ToString(), ct);
        if (ip is null) return ApplyResult.Failed("could not resolve qbittorrent CT IP");
        using var qb = new QbitClient($"http://{ip}:8080");

        // Bootstrap login: desired creds (re-run) → legacy default → journal temp pw.
        var changed = false;
        if (!await qb.LoginAsync(user, pass, ct))
        {
            var bootPw = "adminadmin";
            if (!await qb.LoginAsync("admin", bootPw, ct))
            {
                var journal = await ctx.Exec.InContainerAsync(node, ctid.ToString(),
                    "journalctl -u qbittorrent-nox --no-pager 2>/dev/null | grep -i 'temporary password' | tail -5", ct);
                var temp = ArrExec.ParseQbitTempPassword(journal.Stdout);
                if (temp is null || !await qb.LoginAsync("admin", temp, ct))
                    return ApplyResult.Failed("qbittorrent login failed (desired creds, legacy default, and journal temp password all rejected) — set the WebUI password once, then re-run");
            }
            // Logged in with a bootstrap credential → set the desired creds.
            if (!await qb.SetPreferencesAsync(new { web_ui_username = user, web_ui_password = pass }, ct))
                return ApplyResult.Failed("failed to set qbittorrent WebUI credentials");
            changed = true;
            // Re-login under the new creds for the rest of the session.
            if (!await qb.LoginAsync(user, pass, ct))
                return ApplyResult.Applied("set WebUI creds; re-login needed on next run to finish categories");
        }

        // Default save path.
        var prefs = await qb.GetStringAsync("api/v2/app/preferences", ct);
        using (var doc = JsonDocument.Parse(prefs))
        {
            var savePath = doc.RootElement.TryGetProperty("save_path", out var sp) ? sp.GetString() : null;
            if (savePath?.TrimEnd('/') != "/data/torrents")
            {
                if (!await qb.SetPreferencesAsync(new { save_path = "/data/torrents" }, ct))
                    return ApplyResult.Failed("failed to set qbittorrent save_path");
                changed = true;
            }
        }

        // Categories (add-only).
        var catsJson = await qb.GetStringAsync("api/v2/torrents/categories", ct);
        using var cats = JsonDocument.Parse(catsJson);
        foreach (var (name, path) in Categories)
        {
            if (cats.RootElement.TryGetProperty(name, out _)) continue;
            if (!await qb.PostFormAsync("api/v2/torrents/createCategory",
                    new() { ["category"] = name, ["savePath"] = path }, ct))
                return ApplyResult.Failed($"failed to create category '{name}'");
            changed = true;
        }

        return changed
            ? ApplyResult.Applied($"WebUI creds + save_path + {Categories.Length} categories ensured")
            : ApplyResult.NoChange("qbittorrent already configured (creds, save_path, categories)");
    }
}

// Shared base for Sonarr/Radarr: root folder on /data, qBittorrent download client,
// and self-registration into Prowlarr as an Application (runs AFTER prowlarr — reads
// prowlarr's key via ByName, inverting the dependsOn-first order). All create-if-missing.
public abstract class ArrAppProvisionerBase : IAppProvisioner
{
    public abstract string App { get; }
    protected abstract int Port { get; }              // 8989 sonarr / 7878 radarr
    protected abstract string RootFolder { get; }     // /data/media/{tv,movies}
    protected abstract string QbitCategory { get; }   // tv-sonarr / radarr
    protected abstract int[] SyncCategories { get; }   // newznab cats Prowlarr syncs to this app

    public IEnumerable<string> PlanSteps(Shape s)
    {
        yield return $"ensure root folder {RootFolder}";
        yield return $"ensure qBittorrent download client (category {QbitCategory})";
        yield return $"self-register into Prowlarr as a {App} application (add-only)";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        var ip = await ArrExec.CtIpAsync(ctx, node, ctid.ToString(), ct);
        var key = await ArrExec.ApiKeyAsync(ctx, node, ctid.ToString(), ct);
        if (ip is null || key is null) return ApplyResult.Failed($"could not resolve {App} IP/ApiKey");
        using var self = new ArrClient($"http://{ip}:{Port}", key);
        var changed = 0;

        // 1. Root folder (keyed by path).
        var roots = await self.GetAsync("api/v3/rootfolder", ct);
        var hasRoot = roots.ValueKind == JsonValueKind.Array && roots.EnumerateArray().Any(r =>
            r.TryGetProperty("path", out var p) && p.GetString()?.TrimEnd('/') == RootFolder);
        if (!hasRoot)
        {
            var (ok, body) = await self.PostAsync("api/v3/rootfolder", JsonSerializer.Serialize(new { path = RootFolder }), ct);
            if (!ok) return ApplyResult.Failed($"add root folder failed: {body}");
            changed++;
        }

        // 2. qBittorrent download client.
        var clients = await self.GetAsync("api/v3/downloadclient", ct);
        if (!ArrExec.HasName(clients, "qBittorrent"))
        {
            var qbitIp = await ArrExec.SiblingIpAsync(ctx, "qbittorrent", ct);
            var qbitPass = ctx.Secrets.Get("QBIT_PASSWORD");
            if (qbitIp is null) return ApplyResult.Failed("could not resolve qbittorrent IP for download client");
            if (string.IsNullOrEmpty(qbitPass)) return ApplyResult.Failed("QBIT_PASSWORD not set — needed for the download client");
            var body = JsonSerializer.Serialize(new
            {
                enable = true, protocol = "torrent", priority = 1, name = "qBittorrent",
                implementation = "QBittorrent", implementationName = "qBittorrent", configContract = "QBittorrentSettings",
                fields = new object[]
                {
                    new { name = "host", value = (object)qbitIp },
                    new { name = "port", value = (object)8080 },
                    new { name = "useSsl", value = (object)false },
                    new { name = "username", value = (object)(ctx.Secrets.Get("QBIT_USER") ?? "admin") },
                    new { name = "password", value = (object)qbitPass },
                    new { name = "category", value = (object)QbitCategory },
                },
                tags = Array.Empty<int>(),
            });
            var (ok, resp) = await self.PostAsync("api/v3/downloadclient", body, ct);
            if (!ok) return ApplyResult.Failed($"add download client failed: {resp}");
            changed++;
        }

        // 3. Self-register into Prowlarr (Application). Prowlarr is up already (earlier
        //    in dependsOn order); we read its key/IP via ByName.
        if (!ctx.ByName.TryGetValue("prowlarr", out var pw) || pw.Spec.Node is not { } pn || pw.Spec.Ctid is not { } pc)
            return ApplyResult.Failed("prowlarr sibling not resolvable");
        var pwIp = await ArrExec.CtIpAsync(ctx, pn, pc.ToString(), ct);
        var pwKey = await ArrExec.ApiKeyAsync(ctx, pn, pc.ToString(), ct);
        if (pwIp is null || pwKey is null) return ApplyResult.Failed("could not resolve prowlarr IP/ApiKey");
        using var prowlarr = new ArrClient($"http://{pwIp}:9696", pwKey);
        var apps = await prowlarr.GetAsync("api/v1/applications", ct);
        if (!ArrExec.HasName(apps, App[..1].ToUpperInvariant() + App[1..]))   // "Sonarr" / "Radarr"
        {
            var appName = App[..1].ToUpperInvariant() + App[1..];
            var body = JsonSerializer.Serialize(new
            {
                name = appName, syncLevel = "fullSync",
                implementation = appName, implementationName = appName, configContract = $"{appName}Settings",
                fields = new object[]
                {
                    new { name = "prowlarrUrl", value = (object)$"http://{pwIp}:9696" },
                    new { name = "baseUrl", value = (object)$"http://{ip}:{Port}" },
                    new { name = "apiKey", value = (object)key },
                    new { name = "syncCategories", value = (object)SyncCategories },
                },
                tags = Array.Empty<int>(),
            });
            var (ok, resp) = await prowlarr.PostAsync("api/v1/applications", body, ct);
            if (!ok) return ApplyResult.Failed($"register {appName} into prowlarr failed: {resp}");
            changed++;
        }

        return changed > 0
            ? ApplyResult.Applied($"{App}: root folder + download client + prowlarr app ensured ({changed} change(s))")
            : ApplyResult.NoChange($"{App} already wired (root folder, download client, prowlarr app)");
    }
}

public sealed class SonarrProvisioner : ArrAppProvisionerBase
{
    public override string App => "sonarr";
    protected override int Port => 8989;
    protected override string RootFolder => "/data/media/tv";
    protected override string QbitCategory => "tv-sonarr";
    protected override int[] SyncCategories => new[] { 5000, 5010, 5020, 5030, 5040, 5045, 5050 };
}

public sealed class RadarrProvisioner : ArrAppProvisionerBase
{
    public override string App => "radarr";
    protected override int Port => 7878;
    protected override string RootFolder => "/data/media/movies";
    protected override string QbitCategory => "radarr";
    protected override int[] SyncCategories => new[] { 2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060 };
}

// Prowlarr (CT 5100) — the indexer hub. Two jobs, both add-only:
//   1. carry indexers over from the OLD prowlarr (config.migrateIndexersFrom = old CTID
//      on the same node): GET old /api/v1/indexer → strip server-assigned id → POST any
//      missing-by-name to the new one. Indexers are portable (URLs + keys, no paths).
//   2. ensure a FlareSolverr proxy pointing at the new flaresolverr sibling (5107).
// The new sonarr/radarr register THEMSELVES as Applications (see ArrAppProvisionerBase).
public sealed class ProwlarrProvisioner : IAppProvisioner
{
    public string App => "prowlarr";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        if (s.Spec.Config.TryGetValue("migrateIndexersFrom", out var old))
            yield return $"carry indexers over from old prowlarr CT {old} (add-only, skip-by-name)";
        if (s.Spec.Config.TryGetValue("flaresolverr", out var fs))
            yield return $"ensure FlareSolverr proxy → sibling '{fs}' (add-only)";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        var ip = await ArrExec.CtIpAsync(ctx, node, ctid.ToString(), ct);
        var key = await ArrExec.ApiKeyAsync(ctx, node, ctid.ToString(), ct);
        if (ip is null || key is null) return ApplyResult.Failed("could not resolve prowlarr IP/ApiKey");
        using var self = new ArrClient($"http://{ip}:9696", key);
        var changed = 0;

        // 1. Indexer carry-over from the OLD prowlarr (same node, configured CTID).
        if (s.Spec.Config.TryGetValue("migrateIndexersFrom", out var oldRaw) && oldRaw is not null)
        {
            var oldCtid = oldRaw.ToString()!;
            var oldIp = await ArrExec.CtIpAsync(ctx, node, oldCtid, ct);
            var oldKey = await ArrExec.ApiKeyAsync(ctx, node, oldCtid, ct);
            if (oldIp is null || oldKey is null) return ApplyResult.Failed($"could not reach old prowlarr CT {oldCtid}");
            using var old = new ArrClient($"http://{oldIp}:9696", oldKey);

            var existing = await self.GetAsync("api/v1/indexer", ct);
            var have = existing.ValueKind == JsonValueKind.Array
                ? existing.EnumerateArray().Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string?>();

            var src = await old.GetAsync("api/v1/indexer", ct);
            if (src.ValueKind == JsonValueKind.Array)
                foreach (var idx in src.EnumerateArray())
                {
                    var name = idx.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null || have.Contains(name)) continue;
                    var obj = JsonNode.Parse(idx.GetRawText())!.AsObject();
                    obj.Remove("id");                          // strip the source's server-assigned id
                    var (ok, resp) = await self.PostAsync("api/v1/indexer", obj.ToJsonString(), ct);
                    if (ok) changed++;
                    else Console.WriteLine($"      ⚠ indexer '{name}' not carried over: {resp}");
                }
        }

        // 2. FlareSolverr proxy (add-only).
        if (s.Spec.Config.TryGetValue("flaresolverr", out var fsRaw) && fsRaw is not null)
        {
            var proxies = await self.GetAsync("api/v1/indexerproxy", ct);
            if (!ArrExec.HasName(proxies, "FlareSolverr"))
            {
                var fsIp = await ArrExec.SiblingIpAsync(ctx, fsRaw.ToString()!, ct);
                if (fsIp is null) return ApplyResult.Failed($"could not resolve flaresolverr sibling '{fsRaw}'");
                var body = JsonSerializer.Serialize(new
                {
                    name = "FlareSolverr", implementation = "FlareSolverr",
                    implementationName = "FlareSolverr", configContract = "FlareSolverrSettings",
                    fields = new object[]
                    {
                        new { name = "host", value = (object)$"http://{fsIp}:8191" },
                        new { name = "requestTimeout", value = (object)60 },
                    },
                    tags = Array.Empty<int>(),
                });
                var (ok, resp) = await self.PostAsync("api/v1/indexerproxy", body, ct);
                if (!ok) return ApplyResult.Failed($"add FlareSolverr proxy failed: {resp}");
                changed++;
            }
        }

        return changed > 0
            ? ApplyResult.Applied($"prowlarr: {changed} item(s) added (indexers + FlareSolverr)")
            : ApplyResult.NoChange("prowlarr already has its indexers + FlareSolverr proxy");
    }
}
