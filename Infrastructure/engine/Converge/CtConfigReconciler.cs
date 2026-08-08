using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Update lifecycle (issue #101): reconciles host-level CT config that Proxmox can
// change in place — cores, memory, tags, and NICs — via `pct set`. Idempotent: reads
// `pct config <ctid>`, computes the delta, and only issues `pct set` for fields
// that actually differ. A CT whose config already matches is a no-op.
//
// Deliberately conservative: only fields that are safe to change live are
// reconciled. Disk resize and storage moves are NOT touched here (they're
// disruptive / need their own flows). Anything not declared in the shape
// is left alone — we never strip config we don't own.
//
// NICs (#383): `spec.networks[]` → netN. The community-scripts create path provisions
// exactly ONE interface, so a multi-homed member is created with net0 and picks up
// net1..netN here, on the reconcile pass the runner already runs straight after create.
// Additive by the same rule as everything above: a missing NIC is added and a drifted key
// corrected, but a netN the shape does not mention is never deleted.
public sealed class CtConfigReconciler
{
    private readonly INodeExec _exec;
    public CtConfigReconciler(INodeExec exec) => _exec = exec;

    public async Task<ApplyResult> ReconcileAsync(Shape s, CancellationToken ct = default)
    {
        var sp = s.Spec;
        if (sp.Node is not { } node || sp.Ctid is not { } ctid)
            return ApplyResult.NoChange("no node/ctid to reconcile");

        var read = await _exec.OnNodeAsync(node, $"pct config {ctid}", ct);
        if (!read.Ok) return ApplyResult.Failed($"pct config failed: {read.Stderr}");
        var cfg = ParseConfig(read.Stdout);

        var sets = new List<string>();
        var changed = new List<string>();

        // Cores: set when declared and live differs (or live is unset).
        if (sp.Cores is { } cores)
        {
            var live = cfg.GetValueOrDefault("cores");
            if (live != cores.ToString())
            {
                sets.Add($"--cores {cores}");
                changed.Add($"cores {(live ?? "unset")}→{cores}");
            }
        }

        // Memory: shape MB == `pct config` memory MB. Set when declared and differs.
        if (sp.Memory is { } memory)
        {
            var live = cfg.GetValueOrDefault("memory");
            if (live != memory.ToString())
            {
                sets.Add($"--memory {memory}");
                changed.Add($"memory {(live ?? "unset")}→{memory}");
            }
        }

        // Tags: order-insensitive set comparison, only when the shape declares tags.
        var desiredTags = TagSet.Desired(s);
        if (desiredTags.Count > 0)
        {
            var liveTags = TagSet.Parse(cfg.GetValueOrDefault("tags"));
            if (!desiredTags.SetEquals(liveTags))
            {
                var joined = TagSet.Join(desiredTags);
                // Quote: Proxmox joins tags with ';', which the remote shell would
                // otherwise read as a command separator (#114 smoke test caught this).
                sets.Add($"--tags \"{joined}\"");
                changed.Add($"tags →{joined}");
            }
        }

        // NICs: spec.networks[] → net0..netN, index-positional.
        for (var i = 0; i < sp.Networks.Count; i++)
        {
            var key = $"net{i}";
            var live = cfg.GetValueOrDefault(key);
            // Carry the live MAC forward when the shape doesn't pin one — see LxcNet.Render.
            var liveHwaddr = LxcNet.Parse(live).GetValueOrDefault("hwaddr");
            var desired = LxcNet.Render(sp.Networks[i], i, liveHwaddr);

            if (live is null)
            {
                sets.Add($"--{key} \"{desired}\"");
                changed.Add($"{key} added ({desired})");
            }
            else if (!LxcNet.Matches(live, desired))
            {
                sets.Add($"--{key} \"{desired}\"");
                changed.Add($"{key} {live}→{desired}");
            }
        }

        if (sets.Count == 0)
            return ApplyResult.NoChange("cores/memory/tags/nics already match");

        var res = await _exec.OnNodeAsync(node, $"pct set {ctid} {string.Join(' ', sets)}", ct);
        return res.Ok
            ? ApplyResult.Applied(string.Join(", ", changed))
            : ApplyResult.Failed($"pct set failed: {res.Stderr}");
    }

    // `pct config <ctid>` prints one `key: value` per line (e.g. "cores: 2",
    // "memory: 2048", "tags: iac;media"). Parse into a case-insensitive map; the
    // first ':' splits key from value (values themselves may contain ':').
    private static Dictionary<string, string> ParseConfig(string stdout)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length > 0) map[key] = value;
        }
        return map;
    }
}
