using System.Text.Json;
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
