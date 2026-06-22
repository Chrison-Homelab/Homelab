using System.Text;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// shelfmark (CT 5111) — book search/request hub. Net-new (#186 follow-up). Its
// integrations are driven by environment variables in /etc/shelfmark/.env (systemd
// EnvironmentFile), so this provisioner UPSERTS the managed keys there and restarts the
// `shelfmark` service — idempotent (only writes when a managed key drifts), self-verifying
// (asserts is-active after). The .env is edited with a base64'd python upserter to dodge
// shell quoting of the URLs + the Hardcover JWT (python3 is present in the CT); the desired
// keys travel as base64'd JSON the python decodes, so no quoting of secrets in any layer.
//
// What it wires (per user direction): Prowlarr as a search source (URL + key read LIVE
// from the prowlarr sibling), Hardcover as the metadata provider (token from secrets), and
// the audiobookshelf nav link. Books download to /books (NFS), sideloaded to Kindle/Apple —
// no downstream library/reader tool by design.
public sealed class ShelfmarkProvisioner : IAppProvisioner
{
    public string App => "shelfmark";

    private const string EnvPath = "/etc/shelfmark/.env";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        yield return "enable Prowlarr search source (PROWLARR_URL/API_KEY read live from the prowlarr sibling)";
        yield return "set Hardcover as metadata provider (HARDCOVER_API_KEY from secrets) — skipped if the token is unset";
        if (s.Spec.Config.Str("audiobookLibraryUrl") is { Length: > 0 } u)
            yield return $"set audiobook library nav link → {u}";
        yield return "upsert /etc/shelfmark/.env + restart shelfmark if any managed key drifted (self-verifies is-active)";
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

        // Managed keys.
        var desired = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PROWLARR_ENABLED"] = "true",
            ["PROWLARR_URL"] = $"http://{pwIp}:9696",
            ["PROWLARR_API_KEY"] = pwKey,
        };
        if (s.Spec.Config.Str("audiobookLibraryUrl") is { Length: > 0 } absUrl)
            desired["AUDIOBOOK_LIBRARY_URL"] = absUrl;

        // Hardcover metadata provider — only when its token is present (else leave the
        // provider default; mirrors how other provisioners skip on a missing secret).
        var hc = ctx.Secrets.Get("HARDCOVER_API_KEY");
        if (!string.IsNullOrEmpty(hc))
        {
            desired["HARDCOVER_ENABLED"] = "true";
            desired["HARDCOVER_API_KEY"] = hc!;
            desired["METADATA_PROVIDER"] = "hardcover";
        }

        // Read current .env (a login-shell MOTD banner may precede it — the strict KEY=
        // parse below ignores any non KEY=VALUE line, so the banner can't leak in).
        var cur = await ctx.Exec.InContainerAsync(node, ctid, $"cat {EnvPath} 2>/dev/null", ct);
        var live = ParseEnv(cur.Stdout);
        var drifted = desired.Where(kv => !live.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToList();

        if (drifted.Count == 0)
        {
            // Already converged — still assert the daemon is actually up.
            var act0 = (await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active shelfmark", ct)).Stdout.Trim();
            return act0 == "active"
                ? ApplyResult.NoChange($"shelfmark .env current ({desired.Count} managed keys){HcNote(hc)} + service active")
                : ApplyResult.Failed($"shelfmark .env current but service not active (is-active: {act0})");
        }

        // Upsert managed keys + restart. The desired map travels as base64'd JSON the python
        // decodes (no quoting of the JWT/URLs anywhere); the whole script is base64'd for ssh.
        var jsonB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(desired)));
        var py = string.Join("\n", new[]
        {
            "import json, re, base64",
            "p = \"/etc/shelfmark/.env\"",
            $"upd = json.loads(base64.b64decode(\"{jsonB64}\").decode())",
            "try:",
            "    lines = open(p).read().splitlines()",
            "except FileNotFoundError:",
            "    lines = []",
            "seen = set(); out = []",
            "for ln in lines:",
            "    m = re.match(r'^([A-Za-z0-9_]+)=', ln)",
            "    if m and m.group(1) in upd:",
            "        out.append(m.group(1) + '=' + upd[m.group(1)]); seen.add(m.group(1))",
            "    else:",
            "        out.append(ln)",
            "for k, v in upd.items():",
            "    if k not in seen: out.append(k + '=' + v)",
            "open(p, 'w').write('\\n'.join(out) + '\\n')",
        });
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(py));
        var write = await ctx.Exec.InContainerAsync(node, ctid,
            $"echo {b64} | base64 -d | python3 - && systemctl restart shelfmark && sleep 3", ct);
        if (!write.Ok) return ApplyResult.Failed($"shelfmark .env upsert/restart failed: {write.Stderr}");

        var act = (await ctx.Exec.InContainerAsync(node, ctid, "systemctl is-active shelfmark", ct)).Stdout.Trim();
        if (act != "active")
        {
            var journal = await ctx.Exec.InContainerAsync(node, ctid, "journalctl -u shelfmark --no-pager -n 20 2>/dev/null", ct);
            return ApplyResult.Failed($"shelfmark not active after config (is-active: {act}) — journal:\n{journal.Stdout.Trim()}");
        }
        return ApplyResult.Applied($"shelfmark wired ({string.Join(", ", drifted)}){HcNote(hc)} + restarted & active");
    }

    private static string HcNote(string? hc) => string.IsNullOrEmpty(hc) ? " (Hardcover token unset — skipped)" : "";

    // Parse KEY=VALUE lines (strip surrounding quotes); ignore everything else (MOTD, comments).
    private static Dictionary<string, string> ParseEnv(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^([A-Za-z0-9_]+)=(.*)$");
            if (m.Success) map[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('"', '\'');
        }
        return map;
    }
}
