using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Seerr (CT 5105) — media request UI (the merged Overseerr/Jellyseerr successor). Net-new.
// Wires the new Sonarr + Radarr as request targets via Seerr's REST API (X-Api-Key) so
// approved requests route to the new *arr. Add-only + idempotent (skip if a server of that
// name already exists). GATED: Seerr's admin API 403s until the owner/Plex setup wizard is
// done (manual Plex OAuth — no headless path), so this Skips cleanly when not yet initialized.
//
// Seerr's server config needs an activeProfileId + activeDirectory that are VALID on the arr,
// so we call Seerr's own /settings/<app>/test (which makes Seerr connect to the arr) to fetch
// its quality profiles + root folders, pick the configured profile (config.qualityProfile,
// default HD-1080p) + the first root folder, then POST the server (isDefault). The arr IP/key
// and Seerr's apiKey are all read live.
public sealed class SeerrProvisioner : IAppProvisioner
{
    public string App => "seerr";

    private const string SettingsJson = "/opt/seerr/config/settings.json";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var prof = s.Spec.Config.Str("qualityProfile") ?? "HD-1080p";
        yield return "skip unless Seerr is initialized (owner/Plex setup wizard is manual)";
        yield return $"wire Sonarr as a default request target (profile '{prof}', root from the arr) — add-only";
        yield return $"wire Radarr as a default request target (profile '{prof}', root from the arr) — add-only";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        var ip = await ArrExec.CtIpAsync(ctx, node, ctid, ct);
        var key = await SeerrApiKeyAsync(ctx, node, ctid, ct);
        if (ip is null || key is null) return ApplyResult.Failed("could not resolve seerr IP/apiKey");
        using var seerr = new ArrClient($"http://{ip}:5055", key);

        // Gate: admin settings are locked (403) until the manual Plex/owner wizard completes.
        var pub = await seerr.GetAsync("api/v1/settings/public", ct);
        if (!(pub.TryGetProperty("initialized", out var init) && init.ValueKind == JsonValueKind.True))
            return ApplyResult.Skipped("seerr not initialized — complete the Plex/owner setup wizard first (manual, one-time)");

        var profile = s.Spec.Config.Str("qualityProfile") ?? "HD-1080p";
        var changed = 0;
        changed += await WireArrAsync(ctx, seerr, "sonarr", "Sonarr", 8989, profile,
            new() { ["activeLanguageProfileId"] = 1, ["enableSeasonFolders"] = true }, ct);
        changed += await WireArrAsync(ctx, seerr, "radarr", "Radarr", 7878, profile,
            new() { ["minimumAvailability"] = "released" }, ct);

        return changed > 0
            ? ApplyResult.Applied($"seerr: {changed} request target(s) wired (sonarr/radarr, profile '{profile}')")
            : ApplyResult.NoChange("seerr already has its sonarr + radarr request targets");
    }

    // Add one *arr as a Seerr server (add-only). Uses /test to resolve a valid profile id +
    // root folder, then POSTs the server config as the default. Returns 1 if created, else 0.
    private static async Task<int> WireArrAsync(ConvergeContext ctx, ArrClient seerr, string app, string serverName,
        int port, string profileName, Dictionary<string, object> extra, CancellationToken ct)
    {
        var existing = await seerr.GetAsync($"api/v1/settings/{app}", ct);
        if (ArrExec.HasName(existing, serverName)) return 0;

        var (arrIp, arrKey) = await ArrExec.SiblingIpKeyAsync(ctx, app, ct);
        if (arrIp is null || arrKey is null) throw new InvalidOperationException($"could not resolve {app} IP/apiKey for seerr");

        // Seerr connects to the arr to enumerate quality profiles + root folders.
        var testBody = JsonSerializer.Serialize(new { hostname = arrIp, port, apiKey = arrKey, useSsl = false, baseUrl = "" });
        var (tok, tbody) = await seerr.PostAsync($"api/v1/settings/{app}/test", testBody, ct);
        if (!tok) throw new InvalidOperationException($"seerr {app} test failed: {tbody}");
        using var tdoc = JsonDocument.Parse(tbody);
        var troot = tdoc.RootElement;

        var (profId, profName) = PickProfile(troot, profileName);
        var rootDir = FirstRootFolder(troot) ?? throw new InvalidOperationException($"{app}: no root folder reported by seerr test");

        var payload = new Dictionary<string, object>
        {
            ["name"] = serverName, ["hostname"] = arrIp, ["port"] = port, ["apiKey"] = arrKey,
            ["useSsl"] = false, ["baseUrl"] = "", ["activeProfileId"] = profId, ["activeProfileName"] = profName,
            ["activeDirectory"] = rootDir, ["is4k"] = false, ["isDefault"] = true,
            ["syncEnabled"] = true, ["preventSearch"] = false, ["tags"] = Array.Empty<int>(),
        };
        foreach (var kv in extra) payload[kv.Key] = kv.Value;

        var (ok, resp) = await seerr.PostAsync($"api/v1/settings/{app}", JsonSerializer.Serialize(payload), ct);
        if (!ok) throw new InvalidOperationException($"seerr add {serverName} failed: {resp}");
        return 1;
    }

    // Prefer the named profile (config.qualityProfile); fall back to the first one.
    private static (int id, string name) PickProfile(JsonElement testRoot, string wantName)
    {
        if (testRoot.TryGetProperty("profiles", out var profs) && profs.ValueKind == JsonValueKind.Array)
        {
            JsonElement? first = null;
            foreach (var p in profs.EnumerateArray())
            {
                first ??= p;
                if (p.TryGetProperty("name", out var n) && string.Equals(n.GetString(), wantName, StringComparison.OrdinalIgnoreCase)
                    && p.TryGetProperty("id", out var id))
                    return (id.GetInt32(), n.GetString()!);
            }
            if (first is { } f && f.TryGetProperty("id", out var fid) && f.TryGetProperty("name", out var fn))
                return (fid.GetInt32(), fn.GetString()!);
        }
        throw new InvalidOperationException("seerr test returned no quality profiles");
    }

    private static string? FirstRootFolder(JsonElement testRoot)
    {
        if (testRoot.TryGetProperty("rootFolders", out var rf) && rf.ValueKind == JsonValueKind.Array)
            foreach (var r in rf.EnumerateArray())
                if (r.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } path) return path;
        return null;
    }

    // Seerr's apiKey lives in settings.json (main.apiKey). cat + first-match regex (robust to
    // a login-shell MOTD banner, like ArrExec.ApiKeyAsync does for config.xml).
    private static async Task<string?> SeerrApiKeyAsync(ConvergeContext ctx, string node, string ctid, CancellationToken ct)
    {
        var r = await ctx.Exec.InContainerAsync(node, ctid, $"cat {SettingsJson} 2>/dev/null", ct);
        if (!r.Ok) return null;
        var m = System.Text.RegularExpressions.Regex.Match(r.Stdout, "\"apiKey\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }
}
