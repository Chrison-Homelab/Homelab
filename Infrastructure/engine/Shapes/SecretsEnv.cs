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

    public bool Has(string name) => _values.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v);

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
}
