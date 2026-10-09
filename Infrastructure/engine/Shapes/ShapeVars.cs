using System.Collections;
using System.Text.RegularExpressions;

namespace Homelab.Infrastructure.Shapes;

// `${VAR}` expansion inside a shape's `spec.config`, resolved from secrets.env +
// process env (the same SecretsEnv the provisioners already use for `spec.secrets`).
//
// WHY this exists: a handful of config values are facts about the house rather than
// facts about the infrastructure — the home WAN IPv4 and the DHCPv6-PD prefix. They
// are declared inputs like any other, but this repo is PUBLIC, so the literal has no
// business being committed. `publicIp: ${HOME_WAN_IP}` keeps the shape self-describing
// (the key is still there, still reviewable) while the value arrives at converge time.
//
// SCOPE is deliberately just `spec.config`. Everything else in a shape — node, ctid,
// addresses, mounts — describes the cluster and belongs in git verbatim; widening this
// would invite hiding infrastructure behind opaque names.
//
// UNRESOLVED IS FATAL, never silently empty. Both current consumers fail badly on a
// missing value in ways that look like something else: an absent `publicIp` makes the
// wildcard-DNS step report "skipped" (reads as a no-op, actually leaves LE certs pointing
// nowhere), and a dropped `access.bypass` entry silently re-arms the Cloudflare Access
// one-time-PIN on the whole admin surface. Fail at load with the variable's name instead.
public static class ShapeVars
{
    private static readonly Regex Ref = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    // Expands every `${VAR}` under `config`, in place. `origin` only names the file in
    // the error. Returns the number of substitutions made.
    //
    // `lenient` is for READ-ONLY renderers that never act on config — the dashboard render
    // runs on a runner with no secrets.env and only needs metadata; an unresolved `${VAR}` is
    // left as its literal text instead of failing the load. Converge never passes it.
    public static int Expand(Dictionary<string, object?> config, SecretsEnv vars, string origin, bool lenient = false)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var count = 0;
        WalkMap(config, vars, missing, ref count);
        if (missing.Count > 0 && !lenient)
            throw new InvalidOperationException(
                $"shape '{origin}' references unset variable(s): {string.Join(", ", missing)}. " +
                "These are config values kept out of the public repo — run scripts/secrets-sync.sh " +
                "to regenerate secrets.env, or export them, then re-run.");
        return count;
    }

    private static object? WalkValue(object? value, SecretsEnv vars, SortedSet<string> missing, ref int count)
    {
        switch (value)
        {
            case string s:
                if (!Ref.IsMatch(s)) return s;
                count++;
                return Ref.Replace(s, m =>
                {
                    var name = m.Groups[1].Value;
                    // Has() is empty-aware: a key present but blank (the un-synced
                    // secrets.env case) must read as missing, not as an empty value.
                    if (vars.Has(name)) return vars.Get(name)!;
                    missing.Add(name);
                    return m.Value;
                });

            // YamlDotNet hands back Dictionary<object, object> for nested maps and
            // List<object> for sequences — neither is the typed Dictionary above.
            case IDictionary map:
                foreach (var key in map.Keys.Cast<object>().ToList())
                    map[key] = WalkValue(map[key], vars, missing, ref count);
                return map;

            case IList list:
                for (var i = 0; i < list.Count; i++)
                    list[i] = WalkValue(list[i], vars, missing, ref count);
                return list;

            default:
                return value;
        }
    }

    private static void WalkMap(Dictionary<string, object?> map, SecretsEnv vars, SortedSet<string> missing, ref int count)
    {
        foreach (var key in map.Keys.ToList())
            map[key] = WalkValue(map[key], vars, missing, ref count);
    }
}
