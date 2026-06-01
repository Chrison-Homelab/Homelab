using Homelab.Infrastructure.Shapes;
using ProxmoxSharp;
using ProxmoxSharp.Api;

namespace Homelab.Infrastructure.Converge;

// READ-ONLY state diff for `converge` plan (issue #45). Reads live cluster state
// via ProxmoxDiscovery and compares each desired shape against it. No mutation.
//
// What discovery surfaces per CT (ProxmoxSharp.GuestSnapshot): VmId, Name,
// Status, MaxMem (bytes). It does NOT expose cores or tags today, so those
// desired fields cannot be diffed yet — see LiveCt / comparable fields below.
// We diff what's available (existence, node placement, memory) and stay honest
// about the rest.

public enum ShapeDiffStatus { Create, UpToDate, Drift, Unknown }

// A single config field that differs between desired (shape) and live (cluster).
public sealed record FieldDiff(string Field, string Desired, string Live);

public sealed record ShapeDiff(string Name, string? Ctid, ShapeDiffStatus Status, IReadOnlyList<FieldDiff> Fields)
{
    public static ShapeDiff Create(string name, string? ctid) =>
        new(name, ctid, ShapeDiffStatus.Create, Array.Empty<FieldDiff>());
    public static ShapeDiff UpToDate(string name, string? ctid) =>
        new(name, ctid, ShapeDiffStatus.UpToDate, Array.Empty<FieldDiff>());
    public static ShapeDiff Drift(string name, string? ctid, IReadOnlyList<FieldDiff> fields) =>
        new(name, ctid, ShapeDiffStatus.Drift, fields);
    public static ShapeDiff Unknown(string name, string? ctid) =>
        new(name, ctid, ShapeDiffStatus.Unknown, Array.Empty<FieldDiff>());
}

// One live container as flattened from the cluster snapshot. MaxMemBytes is the
// allocated memory ceiling reported by Proxmox (bytes).
public sealed record LiveCt(int Ctid, string Node, string? Name, string? Status, long? MaxMemBytes);

// The slice of live cluster state the differ needs, keyed by ctid.
public sealed class ClusterState
{
    private readonly Dictionary<int, LiveCt> _byCtid;
    public ClusterState(IEnumerable<LiveCt> cts) =>
        _byCtid = cts.GroupBy(c => c.Ctid).ToDictionary(g => g.Key, g => g.First());

    public bool TryGet(int ctid, out LiveCt ct) => _byCtid.TryGetValue(ctid, out ct!);
    public int Count => _byCtid.Count;
}

// Abstraction over live discovery so plan can degrade gracefully (and so the
// differ is unit-testable without a cluster). Implementations are best-effort:
// on any failure they return null and the caller falls back to intent-only.
public interface IClusterStateProvider
{
    // Returns null when live state can't be obtained (no creds, network down,
    // discovery error). Never throws for those cases.
    Task<ClusterState?> TryGetAsync(CancellationToken ct = default);
}

// Pure diff logic — no I/O. Given a desired shape + live state, classify it.
public static class StateDiffer
{
    public static ShapeDiff Diff(Shape shape, ClusterState state)
    {
        var sp = shape.Spec;
        var name = shape.Metadata.Name;

        // ctid "auto" or non-numeric → we can't correlate to live state by id.
        if (!TryParseCtid(sp.Ctid, out var ctid))
            return ShapeDiff.Unknown(name, sp.Ctid);

        if (!state.TryGet(ctid, out var live))
            return ShapeDiff.Create(name, sp.Ctid);

        var diffs = new List<FieldDiff>();

        // Node placement (desired node vs the node the CT actually lives on).
        if (sp.Node is { } desiredNode && !string.Equals(desiredNode, live.Node, StringComparison.Ordinal))
            diffs.Add(new FieldDiff("node", desiredNode, live.Node));

        // Memory: shape is MB, discovery is bytes. Only compare when both known.
        if (sp.Memory is { } desiredMb && live.MaxMemBytes is { } liveBytes)
        {
            var liveMb = liveBytes / (1024 * 1024);
            if (desiredMb != liveMb)
                diffs.Add(new FieldDiff("memory", $"{desiredMb}MB", $"{liveMb}MB"));
        }

        // NOTE (honest limitation): discovery's GuestSnapshot does not expose
        // cores or tags, so spec.Cores / spec.Tags cannot be diffed here yet.
        // When ProxmoxSharp surfaces per-CT config, add those comparisons.

        return diffs.Count == 0
            ? ShapeDiff.UpToDate(name, sp.Ctid)
            : ShapeDiff.Drift(name, sp.Ctid, diffs);
    }

    private static bool TryParseCtid(string? ctid, out int value)
    {
        value = 0;
        return ctid is not null && int.TryParse(ctid, out value);
    }
}

// Live provider backed by ProxmoxSharp discovery. Best-effort: any failure
// (missing creds, network, API error) → null, so plan degrades to intent-only.
public sealed class ProxmoxClusterStateProvider : IClusterStateProvider
{
    private readonly Func<ProxmoxClientOptions?> _loadOptions;

    public ProxmoxClusterStateProvider(Func<ProxmoxClientOptions?> loadOptions) =>
        _loadOptions = loadOptions;

    public async Task<ClusterState?> TryGetAsync(CancellationToken ct = default)
    {
        try
        {
            var options = _loadOptions();
            if (options is null) return null; // no PVE config → degrade

            var client = ProxmoxApi.Create(options);
            var snapshot = await new ProxmoxDiscovery(client).DiscoverAsync(ct);
            return Flatten(snapshot);
        }
        catch
        {
            // Network / TLS / auth / API errors → best-effort: degrade quietly.
            return null;
        }
    }

    private static ClusterState Flatten(ClusterSnapshot snapshot)
    {
        var cts = new List<LiveCt>();
        foreach (var node in snapshot.Nodes)
            foreach (var lxc in node.Lxc)
                if (lxc.VmId is { } id)
                    cts.Add(new LiveCt((int)id, node.Node, lxc.Name, lxc.Status, lxc.MaxMem));
        return new ClusterState(cts);
    }
}
