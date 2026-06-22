using System.Text;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// shelfmark (CT 5111) — book search/request hub. Net-new (#186 follow-up).
//
// IMPORTANT: shelfmark's settings are SPLIT across stores under CONFIG_DIR=/etc/shelfmark:
//   - per-plugin config files /etc/shelfmark/plugins/<plugin>.json hold a source/provider's
//     OWN keys (incl. its URL + API key — read via config.get in e.g. prowlarr/settings.py),
//   - the general /etc/shelfmark/settings.json holds cross-cutting keys (METADATA_PROVIDER,
//     AUDIOBOOK_LIBRARY_URL, …).
// Env vars only SEED these on first boot; once onboarding_complete is set the JSON wins. So
// this provisioner MERGES each managed key into its CORRECT file (key names verbatim from
// the live files): Prowlarr → plugins/prowlarr_config.json, Hardcover → plugins/hardcover.json,
// the rest → settings.json. (Writing them all to settings.json — the earlier attempt — left
// Prowlarr/Hardcover unconfigured because those plugins read from their own stores.)
//
// Idempotent: a read-only python check reports CHANGED/NOCHANGE across all files; only on
// drift does it stop → merge-write → start (so shelfmark can't flush a stale in-memory copy
// over the write), then assert is-active. The desired map travels as base64'd JSON the python
// merges, so the Hardcover JWT / URLs never hit shell or python quoting.
//
// Wires (per user direction): Prowlarr search source (URL + key read LIVE from the prowlarr
// sibling), Hardcover metadata provider (token from secrets), audiobookshelf nav link.
// Books download to /books (INGEST_DIR default) for Kindle/Apple sideload — no downstream
// library/reader tool by design.
public sealed class ShelfmarkProvisioner : IAppProvisioner
{
    public string App => "shelfmark";

    private const string ProwlarrFile = "/etc/shelfmark/plugins/prowlarr_config.json";
    private const string HardcoverFile = "/etc/shelfmark/plugins/hardcover.json";
    private const string SettingsFile = "/etc/shelfmark/settings.json";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        yield return "enable Prowlarr in plugins/prowlarr_config.json (ENABLED + URL/API_KEY read live from the prowlarr sibling)";
        yield return "enable Hardcover in plugins/hardcover.json (ENABLED + API_KEY from secrets) — skipped if the token is unset";
        if (s.Spec.Config.Str("audiobookLibraryUrl") is { Length: > 0 } u)
            yield return $"set METADATA_PROVIDER + audiobook nav link → {u} in settings.json";
        yield return "merge each key into its correct store + restart shelfmark only on drift (self-verifies is-active)";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid) return ApplyResult.Failed("missing node/ctid");

        // Prowlarr (search source) — IP + API key read live from the sibling.
        if (!ctx.ByName.TryGetValue("prowlarr", out var pw) || pw.Spec.Node is not { } pn || pw.Spec.Ctid is not { } pc)
            return ApplyResult.Failed("prowlarr sibling not resolvable");
        var pwIp = await ArrExec.CtIpAsync(ctx, pn, pc, ct);
        var pwKey = await ArrExec.ApiKeyAsync(ctx, pn, pc, ct);
        if (pwIp is null || pwKey is null) return ApplyResult.Failed("could not resolve prowlarr IP/ApiKey");

        // Route each managed key to its correct store (typed — booleans land as JSON true).
        var prowlarr = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["PROWLARR_ENABLED"] = true,
            ["PROWLARR_URL"] = $"http://{pwIp}:9696",
            ["PROWLARR_API_KEY"] = pwKey,
        };
        var general = new Dictionary<string, object>(StringComparer.Ordinal);
        if (s.Spec.Config.Str("audiobookLibraryUrl") is { Length: > 0 } absUrl)
            general["AUDIOBOOK_LIBRARY_URL"] = absUrl;

        var spec = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
        {
            [ProwlarrFile] = prowlarr,
        };
        var hc = ctx.Secrets.Get("HARDCOVER_API_KEY");
        if (!string.IsNullOrEmpty(hc))
        {
            spec[HardcoverFile] = new(StringComparer.Ordinal) { ["HARDCOVER_ENABLED"] = true, ["HARDCOVER_API_KEY"] = hc! };
            general["METADATA_PROVIDER"] = "hardcover";
        }
        if (general.Count > 0) spec[SettingsFile] = general;

        // The python merger embeds the {file → keys} spec as base64'd JSON and takes a mode
        // arg (check = report only; write = merge). It reads/writes each file itself, so MOTD
        // banners and JWT/URL quoting are non-issues; merge preserves each file's other keys.
        var specB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(spec)));
        var py = string.Join("\n", new[]
        {
            "import json, base64, sys",
            $"spec = json.loads(base64.b64decode(\"{specB64}\").decode())",
            "changed = False",
            "for p, upd in spec.items():",
            "    try:",
            "        cur = json.load(open(p))",
            "    except Exception:",
            "        cur = {}",
            "    new = dict(cur); new.update(upd)",
            "    if new != cur:",
            "        changed = True",
            "        if len(sys.argv) > 1 and sys.argv[1] == \"write\":",
            "            json.dump(new, open(p, \"w\"), indent=2)",
            "print(\"WROTE\" if (changed and len(sys.argv) > 1 and sys.argv[1] == \"write\") else (\"CHANGED\" if changed else \"NOCHANGE\"))",
        });
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(py));

        // Read-only drift check (python reads the files itself → no MOTD/quoting concerns).
        var check = await ctx.Exec.InContainerAsync(node, ctid, $"echo {b64} | base64 -d | python3 - check", ct);
        if (!check.Ok) return ApplyResult.Failed($"shelfmark settings check failed: {check.Stderr}");

        var keys = string.Join(", ", spec.Values.SelectMany(d => d.Keys));
        if (check.Stdout.Contains("NOCHANGE"))
        {
            var act0 = (await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active shelfmark", ct)).Stdout.Trim();
            return act0 == "active"
                ? ApplyResult.NoChange($"shelfmark settings current ({spec.Count} store(s)){HcNote(hc)} + service active")
                : ApplyResult.Failed($"shelfmark settings current but service not active (is-active: {act0})");
        }

        // Drift → stop, merge-write, start (stop/start so shelfmark reloads from disk and
        // can't flush a stale in-memory copy over our write), then assert is-active.
        var write = await ctx.Exec.InContainerAsync(node, ctid,
            $"systemctl stop shelfmark; echo {b64} | base64 -d | python3 - write; systemctl start shelfmark; sleep 4", ct);
        if (!write.Ok || !write.Stdout.Contains("WROTE"))
            return ApplyResult.Failed($"shelfmark settings.json merge/restart failed: {write.Stdout} {write.Stderr}");

        var act = (await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active shelfmark", ct)).Stdout.Trim();
        if (act != "active")
        {
            var journal = await ctx.Exec.InContainerAsync(node, ctid, "journalctl -u shelfmark --no-pager -n 20 2>/dev/null", ct);
            return ApplyResult.Failed($"shelfmark not active after settings merge (is-active: {act}) — journal:\n{journal.Stdout.Trim()}");
        }
        return ApplyResult.Applied($"shelfmark settings merged ({keys}){HcNote(hc)} + restarted & active");
    }

    private static string HcNote(string? hc) => string.IsNullOrEmpty(hc) ? " (Hardcover token unset — skipped)" : "";
}
