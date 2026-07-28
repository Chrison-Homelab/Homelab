using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Parses and validates `--only <member>[,<member>...]` (issue #306).
//
// WHY THIS EXISTS: converge is otherwise all-or-nothing across a stack, which is a
// hazard for any stack holding an ADOPTED member. Adopted guests (VM 2000 Home
// Assistant, CT 4100) reappear as perpetual drift, so every apply aimed at one new
// member also wanted to write to them. Phases 1 and 2a of the podman migration were
// both applied from a hand-built isolated stack directory to dodge that — a
// workaround that silently loses `dependsOn` ordering and stack defaults.
//
// Filtering happens AFTER the stack is loaded and ordered, never before, so a
// selected member keeps its merged stack defaults and its position in the full
// dependency order. What the filter changes is only which members get ACTED ON.
public static class MemberSelection
{
    // Extracts the requested names from an argv. Accepts `--only a,b`, `--only=a,b`,
    // and repetition (`--only a --only b`). Returns null when the flag is absent —
    // distinct from an empty selection, which is an error.
    public static IReadOnlyList<string>? Parse(string[] args)
    {
        List<string>? names = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? raw = null;

            if (a.StartsWith("--only=", StringComparison.Ordinal))
                raw = a["--only=".Length..];
            else if (string.Equals(a, "--only", StringComparison.Ordinal))
            {
                // A bare trailing `--only`, or one followed by another flag, is a
                // usage error rather than "select nothing" — see Resolve's guard.
                raw = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "";
            }

            if (raw is null) continue;
            names ??= new List<string>();
            names.AddRange(raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
        return names;
    }

    // Narrows an already-ordered member list to the selection, preserving order.
    //
    // An unknown name is FATAL. Silently converging nothing on a typo would report
    // "0 applied" and exit 0, which reads as success — the single worst outcome for
    // a flag whose whole purpose is to make apply safer.
    public static (IReadOnlyList<Shape> Lxc, IReadOnlyList<VmShape> Vm) Resolve(
        IReadOnlyList<Shape> ordered,
        IReadOnlyList<VmShape> vmMembers,
        IReadOnlyList<string>? only)
    {
        if (only is null) return (ordered, vmMembers);

        if (only.Count == 0)
            throw new InvalidOperationException("--only requires at least one member name.");

        var known = new HashSet<string>(
            ordered.Select(s => s.Metadata.Name).Concat(vmMembers.Select(v => v.Metadata.Name)),
            StringComparer.Ordinal);

        var unknown = only.Where(n => !known.Contains(n)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"--only: no such member(s): {string.Join(", ", unknown)}. " +
                $"Members of this stack: {string.Join(", ", known.OrderBy(n => n, StringComparer.Ordinal))}");

        var wanted = new HashSet<string>(only, StringComparer.Ordinal);
        return (ordered.Where(s => wanted.Contains(s.Metadata.Name)).ToList(),
                vmMembers.Where(v => wanted.Contains(v.Metadata.Name)).ToList());
    }

    // Dependencies of a selected member that were NOT themselves selected.
    //
    // These are the ones `--only` quietly assumes are already converged. The issue
    // (#306) weighed implicitly pulling them in against failing loudly and chose
    // loud: pulling them in would mean `--only` writes to members the operator did
    // not name, which is the exact surprise it exists to prevent. So callers check
    // these exist live and refuse the member if not.
    public static IReadOnlyList<string> UnselectedDependencies(IReadOnlyList<Shape> selected)
    {
        var chosen = new HashSet<string>(selected.Select(s => s.Metadata.Name), StringComparer.Ordinal);
        return selected.SelectMany(s => s.Spec.DependsOn)
                       .Where(d => !chosen.Contains(d))
                       .Distinct(StringComparer.Ordinal)
                       .OrderBy(d => d, StringComparer.Ordinal)
                       .ToList();
    }
}
