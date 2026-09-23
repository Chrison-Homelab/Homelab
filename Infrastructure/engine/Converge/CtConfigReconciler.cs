using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Update lifecycle (issue #101, #478): reconciles host-level CT config that Proxmox can
// change in place — cores, memory, swap, tags, and NICs — via `pct set`, plus rootfs
// GROWTH via `pct resize`. Idempotent: reads `pct config <ctid>`, computes the delta, and
// only issues a command for fields that actually differ. A CT whose config already matches
// is a no-op.
//
// Deliberately conservative: only changes that are safe to apply live are made. All of
// cores/memory/swap and an ext4-on-LVM-thin grow are online and non-disruptive. Anything
// not declared in the shape is left alone — we never strip config we don't own.
//
// Disk (#478): rootfs GROWTH only — `pct resize <ctid> rootfs +<delta>G`. A SHRINK is
// refused, never attempted: LVM-thin cannot shrink a mounted filesystem and trying is
// destructive. A declared shrink is surfaced as an operator-resolved condition (Skipped
// with a ⚠), not silently ignored and not a hard failure that would skip the member's
// remaining steps. Storage MOVES (a different `storage:`) are still out of scope here.
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

        // Swap: shape MB == `pct config` swap MB. Set when declared and differs. Live-safe.
        if (sp.Swap is { } swap)
        {
            var live = cfg.GetValueOrDefault("swap");
            if (live != swap.ToString())
            {
                sets.Add($"--swap {swap}");
                changed.Add($"swap {(live ?? "unset")}→{swap}");
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
                // A rename silently breaks the guest's networking (LxcNet.RenamesInterface
                // explains how), and `pct config` afterwards looks fine — so say it loudly
                // here rather than leaving the operator to discover it via a container that
                // has no IPv4. Not automated: restarting a guest's network is the operator's
                // call, and on a member like Home Assistant it is not a quiet action.
                var note = LxcNet.RenamesInterface(live, desired)
                    ? " ⚠ RENAME — guest keeps a stale /etc/network/interfaces stanza for the old"
                      + " name and will lose IPv4 on ALL interfaces until it is removed and the"
                      + " guest network is restarted"
                    : "";
                changed.Add($"{key} {live}→{desired}{note}");
            }
        }

        // Disk: rootfs GROWTH only, via a separate `pct resize` (not a `pct set` field).
        // Live size is parsed from the rootfs entry, e.g. "local-lvm:vm-3003-disk-0,size=32G".
        // A shrink is refused and reported; growth issues `pct resize rootfs +<delta>G` — the
        // '+' is required, an absolute target equal-or-below live is a Proxmox no-op.
        string? shrinkRefused = null;
        var growResize = false;
        if (sp.Disk is { } desiredGb && ParseRootfsSizeGb(cfg.GetValueOrDefault("rootfs")) is { } liveGb)
        {
            if (desiredGb > liveGb)
            {
                var deltaGb = desiredGb - liveGb;
                var resize = await _exec.OnNodeAsync(node, $"pct resize {ctid} rootfs +{deltaGb}G", ct);
                if (!resize.Ok)
                    return ApplyResult.Failed($"pct resize failed: {resize.Stderr}");
                growResize = true;
                changed.Add($"disk {liveGb}G→{desiredGb}G (grew +{deltaGb}G)");
            }
            else if (desiredGb < liveGb)
            {
                // Never attempt a shrink — LVM-thin can't shrink a mounted fs and trying is
                // destructive. Surface it so the operator resolves it (fix the shape, or move
                // the guest deliberately), the way a retired-but-live member is reported.
                shrinkRefused = $"disk {liveGb}G declared {desiredGb}G — ⚠ SHRINK REFUSED "
                    + "(LVM-thin cannot shrink a mounted filesystem; resolve by hand or correct the shape)";
            }
        }

        if (sets.Count == 0)
        {
            if (growResize) return ApplyResult.Applied(string.Join(", ", changed));
            if (shrinkRefused is not null) return ApplyResult.Skipped(shrinkRefused);
            return ApplyResult.NoChange("cores/memory/swap/tags/nics/disk already match");
        }

        var res = await _exec.OnNodeAsync(node, $"pct set {ctid} {string.Join(' ', sets)}", ct);
        if (!res.Ok) return ApplyResult.Failed($"pct set failed: {res.Stderr}");

        // A refused shrink rides along on the message so it isn't lost behind a successful set.
        var msg = string.Join(", ", changed);
        if (shrinkRefused is not null) msg += $"; {shrinkRefused}";
        return ApplyResult.Applied(msg);
    }

    // Parse the allocated rootfs size in whole GB from a `pct config` rootfs entry, e.g.
    // "local-lvm:vm-3003-disk-0,size=32G" → 32. Returns null when absent or not expressed
    // in G (M/T are not something we grow rootfs in; refuse to guess rather than mis-resize).
    internal static int? ParseRootfsSizeGb(string? rootfs)
    {
        if (string.IsNullOrWhiteSpace(rootfs)) return null;
        foreach (var part in rootfs.Split(','))
        {
            var p = part.Trim();
            if (!p.StartsWith("size=", StringComparison.OrdinalIgnoreCase)) continue;
            var val = p["size=".Length..].Trim();
            if (val.EndsWith("G", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(val[..^1], out var gb)) return gb;
            return null; // present but not in G — don't guess
        }
        return null;
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
