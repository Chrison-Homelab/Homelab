using System.Text;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// plex (CT 5008) — the media server. ADOPTED legacy CT; see stacks/Media/plex.lxc.yaml.
//
// WHY THIS EXISTS (#332): Plex's server settings lived ONLY inside the container, in
// `Preferences.xml`. Nothing declared them, so a rebuild would silently come back with
// Plex defaults — and at least one of the live settings is a deliberate NON-default that
// materially changes playback behaviour:
//
//   TranscoderCanOnlyRemuxVideo = 1   ("Disable video stream transcoding", default 0)
//
// That is an intentional choice: this box is a 4-core i5-6500T, so video transcoding is
// refused outright and clients get direct play / remux / audio-only transcode. Losing it in
// a rebuild would let a weak server start accepting 4K video transcodes it cannot serve.
//
// HOW: reconciled through the Plex HTTP API (`/:/prefs`), not by editing Preferences.xml.
// Plex holds that file open and rewrites it on shutdown, so an edit underneath a running
// server is liable to be discarded; the API applies live and needs no restart. The token is
// read from Preferences.xml INSIDE the container by the helper script, so it never crosses
// the exec boundary or reaches a shell argument.
//
// ADD/UPDATE ONLY, like the rest of the converge model: only the keys DECLARED under
// `config.prefs` are compared and set. Anything else Plex holds is left alone — this
// provisioner never resets a setting to default and never removes one.
public sealed class PlexProvisioner : IAppProvisioner
{
    public string App => "plex";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var prefs = DeclaredPrefs(s);
        if (prefs.Count == 0)
        {
            yield return "no config.prefs declared — Plex settings left entirely untouched";
            yield break;
        }
        yield return $"reconcile {prefs.Count} Plex preference(s) via the /:/prefs API (add/update only): "
                     + string.Join(", ", prefs.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // config.prefs — a map of Plex preference id → desired value. Values are normalised to
    // the wire form Plex expects: booleans become 1/0, everything else is stringified.
    internal static Dictionary<string, string> DeclaredPrefs(Shape s)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!s.Spec.Config.TryGetValue("prefs", out var raw) || raw is not System.Collections.IDictionary d)
            return result;
        foreach (System.Collections.DictionaryEntry e in d)
        {
            if (e.Key?.ToString() is not { Length: > 0 } k) continue;
            result[k] = Normalise(e.Value);
        }
        return result;
    }

    // Plex's API takes bools as 1/0. A YAML `true` arriving as "True" would be rejected, and
    // — worse — would compare unequal to the "1" Plex reports back, so every run would look
    // like drift and re-PUT forever.
    private static string Normalise(object? v) => v switch
    {
        bool b => b ? "1" : "0",
        null => "",
        _ => v.ToString() switch
        {
            "true" or "True" => "1",
            "false" or "False" => "0",
            var s => s ?? "",
        },
    };

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.Failed("missing node/ctid");

        var prefs = DeclaredPrefs(s);
        if (prefs.Count == 0)
            return ApplyResult.NoChange("no config.prefs declared — Plex settings untouched");

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(HelperScript(prefs)));

        // check → report drift only; write → PUT the differing keys.
        var check = await ctx.Exec.InContainerAsync(node, ctid, $"echo {b64} | base64 -d | python3 - check", ct);
        if (!check.Ok || check.Stdout.Length == 0)
            return ApplyResult.Failed($"plex prefs check failed: {Trim(check.Stdout)} {Trim(check.Stderr)}");
        if (check.Stdout.Contains("UNREACHABLE"))
            return ApplyResult.Failed("plex prefs: server not answering on :32400 (is plexmediaserver running?)");
        if (check.Stdout.Contains("NOTOKEN"))
            return ApplyResult.Failed("plex prefs: no PlexOnlineToken in Preferences.xml — server not signed in");
        if (check.Stdout.Contains("NOCHANGE"))
            return ApplyResult.NoChange($"plex prefs current ({prefs.Count} declared: {string.Join(", ", prefs.Keys)})");

        var write = await ctx.Exec.InContainerAsync(node, ctid, $"echo {b64} | base64 -d | python3 - write", ct);
        if (!write.Ok || !write.Stdout.Contains("WROTE"))
            return ApplyResult.Failed($"plex prefs write failed: {Trim(write.Stdout)} {Trim(write.Stderr)}");

        return ApplyResult.Applied($"plex prefs updated — {Marker(write.Stdout, "WROTE")}");
    }

    private static string Trim(string s) => s.Replace("\r", "").Trim();

    // `pct exec` into a community-scripts CT prints an ANSI-coloured MOTD banner ahead of the
    // command's own output, so the whole stdout is never the answer. Pull just the line the
    // helper emitted — taking stdout wholesale put "Plex LXC Container" in the result message.
    private static string Marker(string stdout, string prefix) =>
        Trim(stdout).Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))
            ?.Substring(prefix.Length).Trim()
        ?? "(no detail returned)";

    // Runs INSIDE the CT. Reads the token locally, GETs /:/prefs, compares only the declared
    // keys, and (in write mode) PUTs just the ones that differ.
    private static string HelperScript(Dictionary<string, string> prefs)
    {
        var json = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(prefs)));
        return string.Join("\n", new[]
        {
            "import base64, json, re, sys, urllib.parse, urllib.request",
            "import xml.etree.ElementTree as ET",
            $"want = json.loads(base64.b64decode(\"{json}\").decode())",
            "PREF = \"/var/lib/plexmediaserver/Library/Application Support/Plex Media Server/Preferences.xml\"",
            "try:",
            "    m = re.search(r'PlexOnlineToken=\"([^\"]*)\"', open(PREF).read())",
            "except Exception:",
            "    m = None",
            "if not m or not m.group(1):",
            "    print('NOTOKEN'); sys.exit(0)",
            "tok = m.group(1)",
            "def call(path, method='GET'):",
            "    r = urllib.request.Request('http://127.0.0.1:32400' + path, method=method,",
            "                               headers={'X-Plex-Token': tok})",
            "    return urllib.request.urlopen(r, timeout=15).read()",
            "try:",
            "    root = ET.fromstring(call('/:/prefs'))",
            "except Exception:",
            "    print('UNREACHABLE'); sys.exit(0)",
            "live = {s.get('id'): (s.get('value') or '') for s in root}",
            "drift = {k: v for k, v in want.items() if live.get(k, None) != v}",
            // A declared key Plex doesn't know is a typo in the shape, not drift to hammer at.
            "unknown = [k for k in want if k not in live]",
            "if unknown:",
            "    print('UNKNOWN ' + ','.join(sorted(unknown))); sys.exit(1)",
            "if not drift:",
            "    print('NOCHANGE'); sys.exit(0)",
            "desc = ', '.join(f\"{k}: {live.get(k)!r}->{v!r}\" for k, v in sorted(drift.items()))",
            "if len(sys.argv) > 1 and sys.argv[1] == 'write':",
            "    for k, v in drift.items():",
            "        call('/:/prefs?' + urllib.parse.urlencode({k: v}), method='PUT')",
            "    print('WROTE ' + desc)",
            "else:",
            "    print('CHANGED ' + desc)",
        });
    }
}
