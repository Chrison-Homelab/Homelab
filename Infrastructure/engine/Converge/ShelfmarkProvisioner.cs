using System.Text;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// shelfmark (CT 5111) — book search/request hub. Net-new (#186 follow-up).
//
// IMPORTANT: shelfmark's integration settings live in its authoritative runtime store
// /etc/shelfmark/settings.json (CONFIG_DIR), NOT the .env. Env vars only SEED that JSON on
// first boot; once onboarding_complete is set, settings.json wins. So this provisioner
// MERGES the managed keys into settings.json (the keys are the same UPPERCASE names the env
// doc uses, e.g. METADATA_PROVIDER, PROWLARR_ENABLED) and restarts the `shelfmark` service.
//
// Idempotent: a read-only python check reports CHANGED/NOCHANGE; only on drift does it
// stop → write (merge) → start (so shelfmark can't flush a stale in-memory copy over our
// write), then assert is-active. The desired map travels as base64'd JSON the python merges,
// so the Hardcover JWT / URLs never hit shell or python quoting.
//
// Wires (per user direction): Prowlarr search source (URL + key read LIVE from the prowlarr
// sibling), Hardcover metadata provider (token from secrets), audiobookshelf nav link.
// Books download to /books (INGEST_DIR default) for Kindle/Apple sideload — no downstream
// library/reader tool by design.
public sealed class ShelfmarkProvisioner : IAppProvisioner
{
    public string App => "shelfmark";

    private const string SettingsPath = "/etc/shelfmark/settings.json";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        yield return "enable Prowlarr search source in settings.json (URL/API_KEY read live from the prowlarr sibling)";
        yield return "set Hardcover as metadata provider (HARDCOVER_API_KEY from secrets) — skipped if the token is unset";
        if (s.Spec.Config.Str("audiobookLibraryUrl") is { Length: > 0 } u)
            yield return $"set audiobook library nav link → {u}";
        yield return "merge into /etc/shelfmark/settings.json + restart shelfmark only on drift (self-verifies is-active)";
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

        // Managed settings (typed — booleans land as JSON true, not "true").
        var desired = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["PROWLARR_ENABLED"] = true,
            ["PROWLARR_URL"] = $"http://{pwIp}:9696",
            ["PROWLARR_API_KEY"] = pwKey,
        };
        if (s.Spec.Config.Str("audiobookLibraryUrl") is { Length: > 0 } absUrl)
            desired["AUDIOBOOK_LIBRARY_URL"] = absUrl;

        var hc = ctx.Secrets.Get("HARDCOVER_API_KEY");
        if (!string.IsNullOrEmpty(hc))
        {
            desired["HARDCOVER_ENABLED"] = true;
            desired["HARDCOVER_API_KEY"] = hc!;
            desired["METADATA_PROVIDER"] = "hardcover";
        }

        // The python merger embeds the desired map as base64'd JSON and takes a mode arg
        // (check = report only; write = merge into settings.json). It reads/writes the file
        // itself, so MOTD banners and JWT/URL quoting are non-issues.
        var desiredB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(desired)));
        var py = string.Join("\n", new[]
        {
            "import json, base64, sys",
            "p = \"/etc/shelfmark/settings.json\"",
            $"desired = json.loads(base64.b64decode(\"{desiredB64}\").decode())",
            "try:",
            "    cur = json.load(open(p))",
            "except Exception:",
            "    cur = {}",
            "new = dict(cur); new.update(desired)",
            "if new == cur:",
            "    print(\"NOCHANGE\"); sys.exit(0)",
            "if len(sys.argv) > 1 and sys.argv[1] == \"write\":",
            "    json.dump(new, open(p, \"w\"), indent=4); print(\"WROTE\")",
            "else:",
            "    print(\"CHANGED\")",
        });
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(py));

        // Read-only drift check (python reads the file itself → no MOTD/quoting concerns).
        var check = await ctx.Exec.InContainerAsync(node, ctid, $"echo {b64} | base64 -d | python3 - check", ct);
        if (!check.Ok) return ApplyResult.Failed($"shelfmark settings check failed: {check.Stderr}");

        var keys = string.Join(", ", desired.Keys);
        if (check.Stdout.Contains("NOCHANGE"))
        {
            var act0 = (await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active shelfmark", ct)).Stdout.Trim();
            return act0 == "active"
                ? ApplyResult.NoChange($"shelfmark settings.json current ({desired.Count} keys){HcNote(hc)} + service active")
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
