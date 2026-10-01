namespace Homelab.Infrastructure.Shapes;

// Reads the gitignored secrets.env (KEY=VALUE) — the chosen secrets backend
// (BL-010). Values are never logged; only presence/absence is surfaced.
public sealed class SecretsEnv
{
    private readonly Dictionary<string, string> _values;

    private SecretsEnv(Dictionary<string, string> values) => _values = values;

    public static SecretsEnv Load(string? path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path is not null && File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim().Trim('"', '\'');
                map[key] = val;
            }
        }
        // Process env overrides/augments the file (e.g. CI injects via env).
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v && !map.ContainsKey(k)) map[k] = v;
        }
        return new SecretsEnv(map);
    }

    // ── #609 step 4: one resolver for every converge ────────────────────────────────
    // Precedence, first hit wins:
    //   1. OpenBao (secret/homelab/*) when reachable and unsealed — the store itself, so a stale
    //      or half-filled secrets.env can no longer decide what a converge sees;
    //   2. the process environment — CI, where the job has already exported every value (and
    //      where the engine therefore does not contact OpenBao itself);
    //   3. secrets.env — ONLY when OpenBao is unavailable (sealed after a restart, off-LAN);
    //   4. literal values in secrets.env.template — the non-secret settings committed in git.
    // `Source` says which of these supplied the run, for the one line the converge prints.
    public string Source { get; private set; } = "secrets.env + process env";

    public static SecretsEnv Resolve(string? secretsPath, string? templatePath,
        Func<(Dictionary<string, string>? Map, string? Reason)>? openBao = null, bool? ci = null)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = new List<string>();
        var inCi = ci ?? string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal);

        Dictionary<string, string>? bao = null;
        string? why = null;
        if (inCi) why = "CI (the job exported its secrets already)";
        else (bao, why) = (openBao ?? (() => OpenBaoSource.TryLoad(templatePath is null ? null : Path.GetDirectoryName(templatePath))))();

        if (bao is not null)
        {
            foreach (var (k, v) in bao) map[k] = v;
            parts.Add($"OpenBao ({bao.Count})");
        }
        var envAdded = 0;
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v && !map.ContainsKey(k)) { map[k] = v; envAdded++; }
        if (inCi) parts.Add("process env");
        if (bao is null && secretsPath is not null && File.Exists(secretsPath))
        {
            var n = 0;
            foreach (var (k, v) in Load(secretsPath)._values)
                if (!map.ContainsKey(k)) { map[k] = v; n++; }
            parts.Add($"{Path.GetFileName(secretsPath)} ({n})");
        }
        if (templatePath is not null && File.Exists(templatePath))
        {
            var n = 0;
            foreach (var (k, v) in TemplateLiterals(File.ReadAllLines(templatePath)))
                if (!map.ContainsKey(k)) { map[k] = v; n++; }
            parts.Add($"template literals ({n})");
        }
        var src = string.Join(" + ", parts);
        if (bao is null && !inCi) src = $"⚠ OpenBao not used ({why}) — " + src;
        return new SecretsEnv(map) { Source = src };
    }

    // KEY=value lines with a NON-empty value, read the way the shell that sources secrets.env
    // reads them: a quoted value runs to its closing quote; an unquoted one stops at whitespace,
    // so a trailing `# comment` is not part of it (`UNIFI_LOCAL_HOST=192.168.178.1   # IP on
    // purpose` is 192.168.178.1, which Load()'s plain Trim gets wrong).
    internal static IEnumerable<(string Key, string Value)> TemplateLiterals(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$")) continue;
            var rest = line[(eq + 1)..];
            if (rest.Length == 0) continue;                       // a fill target, not a literal
            string value;
            if (rest[0] is '\'' or '"')
            {
                var close = rest.IndexOf(rest[0], 1);
                if (close < 0) continue;                           // unbalanced: not ours to guess
                value = rest[1..close];
            }
            else
            {
                var ws = rest.IndexOfAny(new[] { ' ', '\t' });
                value = ws < 0 ? rest : rest[..ws];
            }
            if (value.Length > 0) yield return (key, value);
        }
    }

    // Connection settings that code reads straight from the process environment rather than from
    // this object: ProxmoxClientOptions (LoadOptions), UnifiSharp's TryFromEnvironment, the
    // Cloudflare account id, NodeExec's node addresses and SSH key. Before this, `./build.sh` never
    // exported secrets.env, so those reads saw nothing: Preview silently planned "intent-only"
    // and every UniFi reservation was SKIPPED (#617). Only these names, and only where the process
    // has no value of its own, so a deliberate shell export still wins.
    internal static readonly string[] ProcessEnvNames =
    {
        "PROXMOX_BASE_URL", "PROXMOX_TOKEN_ID", "PROXMOX_TOKEN_SECRET", "PROXMOX_VERIFY_TLS",
        "UNIFI_API_KEY", "UNIFI_LOCAL_HOST", "UNIFI_BASE_URL", "UNIFI_VERIFY_TLS",
        "UNIFI_LEGACY_BASE_URL", "UNIFI_USERNAME", "UNIFI_PASSWORD",
        "CF_ACCOUNT_ID", "NODE_SSH_KEY",
    };

    public int ExportConnectionSettings()
    {
        var n = 0;
        foreach (var k in _values.Keys.Where(k => ProcessEnvNames.Contains(k) || k.StartsWith("NODE_ADDR_", StringComparison.Ordinal)).ToList())
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)) && !string.IsNullOrEmpty(_values[k]))
            {
                Environment.SetEnvironmentVariable(k, _values[k]);
                n++;
            }
        return n;
    }

    public bool Has(string name) => _values.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v);

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
}
