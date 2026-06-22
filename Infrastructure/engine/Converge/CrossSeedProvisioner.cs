using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// cross-seed (CT 5106) — finds cross-seedable torrents across the Prowlarr indexers and
// injects matches into the new qBittorrent for extra seeding (no re-download; hardlinked
// on the shared /data export). Net-new, not in the old fleet (#178).
//
// Unlike the *arr provisioners there is NO REST API to converge against: cross-seed reads
// a single config file (/root/.cross-seed/config.js) and runs as the `cross-seed` systemd
// daemon (community-scripts ct/cross-seed.sh: `cross-seed gen-config` then `cross-seed
// daemon`). So this provisioner RENDERS that config from live siblings and restarts the
// daemon — idempotent via a managed marker (like PangolinProvisioner), re-pushing only on
// drift. The config file is written base64-encoded to dodge all shell quoting of the URLs
// (which embed indexer api keys and the qBittorrent password).
//
// Everything is read live at apply — nothing about a sibling's IP/key/password is baked in:
//   torznab[]       ← every Prowlarr indexer's torznab feed (http://prowlarr:9696/{id}/api?apikey=)
//   torrentClients  ← qbittorrent:http://user:pass@qbit:8090 (creds QBIT_USER/QBIT_PASSWORD)
//   dataDirs        ← /data/torrents (shared export; same fs as media → hardlink-injectable)
//   linkDirs        ← /data/torrents/cross-seed (same fs; cross-seed normalises layout here)
public sealed class CrossSeedProvisioner : IAppProvisioner
{
    public string App => "cross-seed";

    private const string ConfigDir = "/root/.cross-seed";
    private const string ConfigPath = ConfigDir + "/config.js";
    private const string LinkDir = "/data/torrents/cross-seed";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        yield return "render /root/.cross-seed/config.js from live siblings (torznab ← prowlarr indexers, torrentClients ← qbittorrent)";
        yield return $"dataDirs /data/torrents + linkDirs {LinkDir} (hardlink), action inject";
        yield return "restart the cross-seed daemon if the config drifted (idempotent via managed marker)";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        // qBittorrent (the download client to inject into) — IP via sibling, creds via secrets.
        // URL-encode user/pass: the password is bw-generated and may carry URL-special chars.
        var qbitIp = await ArrExec.SiblingIpAsync(ctx, "qbittorrent", ct);
        if (qbitIp is null) return ApplyResult.Failed("could not resolve qbittorrent IP");
        var qUser = ctx.Secrets.Get("QBIT_USER") ?? "admin";
        var qPass = ctx.Secrets.Get("QBIT_PASSWORD");
        if (string.IsNullOrEmpty(qPass)) return ApplyResult.Failed("QBIT_PASSWORD not set in secrets.env");
        var qbitClientUrl =
            $"qbittorrent:http://{Uri.EscapeDataString(qUser)}:{Uri.EscapeDataString(qPass)}@{qbitIp}:{ArrExec.QbitWebUiPort}";

        // Prowlarr (indexer hub) — one torznab feed per indexer: http://prowlarr:9696/{id}/api?apikey=KEY.
        if (!ctx.ByName.TryGetValue("prowlarr", out var pw) || pw.Spec.Node is not { } pn || pw.Spec.Ctid is not { } pc)
            return ApplyResult.Failed("prowlarr sibling not resolvable");
        var pwIp = await ArrExec.CtIpAsync(ctx, pn, pc, ct);
        var pwKey = await ArrExec.ApiKeyAsync(ctx, pn, pc, ct);
        if (pwIp is null || pwKey is null) return ApplyResult.Failed("could not resolve prowlarr IP/ApiKey");
        using var prowlarr = new ArrClient($"http://{pwIp}:9696", pwKey);
        var indexers = await prowlarr.GetAsync("api/v1/indexer", ct);
        var ids = indexers.ValueKind == JsonValueKind.Array
            ? indexers.EnumerateArray()
                .Where(e => e.TryGetProperty("id", out _))
                .Select(e => e.GetProperty("id").GetInt32())
                .OrderBy(i => i)                       // stable order → deterministic marker
                .ToList()
            : new List<int>();
        if (ids.Count == 0) return ApplyResult.Failed("prowlarr has no indexers to build torznab feeds from");
        var torznab = ids.Select(id => $"http://{pwIp}:9696/{id}/api?apikey={pwKey}").ToList();

        // Optional declarative overrides; sensible defaults otherwise.
        var matchMode = s.Spec.Config.Str("matchMode") ?? "flexible";
        var action = s.Spec.Config.Str("action") ?? "inject";
        var delay = s.Spec.Config.TryGetValue("delay", out var dv) && int.TryParse(dv?.ToString(), out var di) ? di : 30;

        // Anonymous object → stable property order, so the marker hash is reproducible.
        var config = new
        {
            torznab,
            torrentClients = new[] { qbitClientUrl },
            useClientTorrents = true,           // v6: read torrents straight from qbit (no torrentDir)
            dataDirs = new[] { "/data/torrents" },
            linkDirs = new[] { LinkDir },
            linkType = "hardlink",
            linkCategory = "cross-seed",
            flatLinking = false,
            matchMode,
            action,
            delay,
            includeSingleEpisodes = false,
            includeNonVideos = false,
            duplicateCategories = false,
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var marker = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12].ToLowerInvariant();
        var body = $"// homelab-managed: {marker}\nmodule.exports = {json};\n";

        // Write config only when the marker drifted; the daemon is health-checked either
        // way (below), so a re-run proves BOTH idempotency (marker) AND runtime liveness.
        var cur = await ctx.Exec.InContainerAsync(node, ctid,
            $"grep -m1 '^// homelab-managed:' {ConfigPath} 2>/dev/null || true", ct);
        var curMarker = cur.Stdout.Contains(':') ? cur.Stdout.Split(':', 2)[1].Trim() : "";
        var configChanged = curMarker != marker;

        if (configChanged)
        {
            // Write config + ensure the link dir on the shared export, then restart the daemon
            // (sleep so the follow-up is-active sees the settled state, not 'activating').
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
            var cmd =
                $"mkdir -p {ConfigDir} {LinkDir}; " +
                $"echo {b64} | base64 -d > {ConfigPath}; " +
                "systemctl restart cross-seed; sleep 3";
            var res = await ctx.Exec.InContainerAsync(node, ctid, cmd, ct);
            if (!res.Ok) return ApplyResult.Failed($"write/restart cross-seed failed: {res.Stderr}");
        }

        // Self-verify: the daemon must actually be running. `cross-seed daemon` exits on a
        // bad config (e.g. an unreachable client/indexer), and systemctl restart returns 0
        // before that — so assert is-active and surface the journal tail if it died.
        var health = await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active cross-seed", ct);
        var state = health.Stdout.Trim();
        if (state != "active")
        {
            var journal = await ctx.Exec.InContainerAsync(node, ctid,
                "journalctl -u cross-seed --no-pager -n 20 2>/dev/null", ct);
            return ApplyResult.Failed(
                $"cross-seed daemon not active (is-active: {state}) after {(configChanged ? "config render" : "no-op")} — journal:\n{journal.Stdout.Trim()}");
        }

        return configChanged
            ? ApplyResult.Applied($"cross-seed config rendered ({torznab.Count} torznab feed(s) + qbit inject) + daemon restarted & active (marker {marker})")
            : ApplyResult.NoChange($"cross-seed config current (marker {marker}, {torznab.Count} torznab feed(s)) + daemon active");
    }
}
