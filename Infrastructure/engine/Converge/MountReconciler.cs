using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Update lifecycle (issue #13): applies a shape's `mounts` (+ `hookscript`) to a CT as
// Proxmox mpN entries via `pct set`. The community-scripts create path does NOT provision
// mounts, so this is where the Media stack's shared /data NFS path-bind actually lands.
//
// Idempotent: reads `pct config`, SEMANTICALLY compares each mpN (the volume + the options
// we manage), and only sets the ones that differ — so option reordering or extra options
// Proxmox adds never trigger a needless re-set. Anything not declared is left alone.
//
// Scope: PATH-BIND mounts — `type: nfs` with a `source` subpath (rendered under the
// storage's host mount, /mnt/pve/<storage>/<source>), and `type: bind` (a raw host path).
// These are deterministic + idempotent. Allocated-volume mounts (nfs/volume with `size`)
// and `device` mounts are NOT rendered here yet — they need volume-id tracking — and are
// reported as skipped rather than mis-applied.
//
// NOTE: the NAS-safety story has two more pieces NOT done here (deploy/host concerns, not
// `pct set`): the immutable-underlying-mountpoint guard and installing the per-CT
// nas-watchdog. See task #13 / the Media README.
public sealed class MountReconciler
{
    private readonly INodeExec _exec;
    public MountReconciler(INodeExec exec) => _exec = exec;

    public async Task<ApplyResult> ReconcileAsync(Shape s, CancellationToken ct = default)
    {
        var sp = s.Spec;
        if (sp.Node is not { } node || sp.Ctid is not { } ctid)
            return ApplyResult.NoChange("no node/ctid to reconcile");
        if (sp.Mounts.Count == 0 && sp.Hookscript is null)
            return ApplyResult.NoChange("no mounts/hookscript declared");

        var read = await _exec.OnNodeAsync(node, $"pct config {ctid}", ct);
        if (!read.Ok) return ApplyResult.Failed($"pct config failed: {read.Stderr}");
        var cfg = ParseConfig(read.Stdout);

        var sets = new List<string>();
        var changed = new List<string>();
        var skipped = new List<string>();

        for (var i = 0; i < sp.Mounts.Count; i++)
        {
            var m = sp.Mounts[i];
            var rendered = RenderMount(m);
            if (rendered is null)
            {
                skipped.Add($"mp{i} ({m.Type}: needs volume-id tracking — not yet supported)");
                continue;
            }

            var key = $"mp{i}";
            var live = cfg.GetValueOrDefault(key);
            if (live is null || !MountMatches(live, rendered))
            {
                sets.Add($"--{key} {Quote(rendered)}");
                changed.Add($"{key} →{m.Target}");
            }
        }

        if (sp.Hookscript is { } hook)
        {
            if (cfg.GetValueOrDefault("hookscript") != hook)
            {
                sets.Add($"--hookscript {Quote(hook)}");
                changed.Add($"hookscript →{hook}");
            }
        }

        var skipNote = skipped.Count > 0 ? $"; skipped {string.Join(", ", skipped)}" : "";
        if (sets.Count == 0)
            return skipped.Count > 0
                ? ApplyResult.Skipped($"mounts/hookscript already match{skipNote}")
                : ApplyResult.NoChange("mounts/hookscript already match");

        var res = await _exec.OnNodeAsync(node, $"pct set {ctid} {string.Join(' ', sets)}", ct);
        return res.Ok
            ? ApplyResult.Applied(string.Join(", ", changed) + skipNote)
            : ApplyResult.Failed($"pct set failed: {res.Stderr}");
    }

    // Render a mount to its pct mpN value, or null for a type we don't render yet.
    public static string? RenderMount(MountSpec m)
    {
        var volume = m.Type switch
        {
            // path-bind under the storage's host mount (shared subpath — NOT a gated volume)
            "nfs" when !string.IsNullOrEmpty(m.Source) => $"/mnt/pve/{m.Storage}/{m.Source!.TrimStart('/')}",
            // raw host path bind
            "bind" when !string.IsNullOrEmpty(m.Source) => m.Source,
            _ => null, // nfs+size (allocated volume), volume, device → need volume-id tracking
        };
        if (volume is null || string.IsNullOrEmpty(m.Target)) return null;

        var parts = new List<string> { volume, $"mp={m.Target}" };
        if (m.Ro == true) parts.Add("ro=1");
        if (m.Backup == false) parts.Add("backup=0");
        if (m.Acl == true) parts.Add("acl=1");
        if (!string.IsNullOrEmpty(m.Mountoptions)) parts.Add($"mountoptions={m.Mountoptions}");
        return string.Join(',', parts);
    }

    // Match on the volume + only the options WE manage; ignore unmanaged options Proxmox adds.
    public static bool MountMatches(string live, string desired)
    {
        var (liveVol, liveOpts) = ParseMp(live);
        var (wantVol, wantOpts) = ParseMp(desired);
        if (!string.Equals(liveVol, wantVol, StringComparison.Ordinal)) return false;
        foreach (var (k, v) in wantOpts)
            if (liveOpts.GetValueOrDefault(k) != v) return false;
        return true;
    }

    // pct mpN value: "<volume>,mp=<path>,backup=0,…" — first token (no '=') is the volume.
    private static (string Vol, Dictionary<string, string> Opts) ParseMp(string raw)
    {
        var vol = "";
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in raw.Split(','))
        {
            var t = p.Trim();
            if (t.Length == 0) continue;
            var eq = t.IndexOf('=');
            if (eq < 0) vol = t;
            else opts[t[..eq].Trim()] = t[(eq + 1)..].Trim();
        }
        return (vol, opts);
    }

    private static string Quote(string v) => v.Contains(' ') ? $"\"{v}\"" : v;

    private static Dictionary<string, string> ParseConfig(string stdout)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            if (key.Length > 0) map[key] = line[(colon + 1)..].Trim();
        }
        return map;
    }
}
