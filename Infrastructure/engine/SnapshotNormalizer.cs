using ProxmoxSharp;

namespace Homelab.Infrastructure;

/// <summary>
/// Makes a <see cref="ClusterSnapshot"/> deterministic so that re-running
/// <c>discover</c> against an unchanged cluster yields byte-identical JSON.
///
/// This is a prerequisite for the drift-detection workflow: without it Proxmox
/// returns collections (guests, storage, network) in arbitrary order and a
/// volatile node <c>uptime</c>, so every run produced a different file and the
/// workflow opened a "refresh" PR full of incidental churn even when nothing
/// had actually changed.
///
/// Normalizations:
///  - Nodes sorted by name; guests by VmId; storage by name; network by iface.
///  - Storage <c>Content</c> token set sorted (Proxmox joins it in random order).
///  - Volatile runtime fields (node <c>Uptime</c>) dropped — they are not part of
///    the cluster's configured state and change on every read.
/// </summary>
public static class SnapshotNormalizer
{
    public static ClusterSnapshot Normalize(ClusterSnapshot snapshot) => new()
    {
        Nodes = snapshot.Nodes
            .OrderBy(n => n.Node, StringComparer.Ordinal)
            .Select(NormalizeNode)
            .ToList(),
    };

    private static NodeSnapshot NormalizeNode(NodeSnapshot node) => node with
    {
        Uptime = null, // volatile — excluded from the committed snapshot
        Lxc = node.Lxc.OrderBy(g => g.VmId).ToList(),
        Qemu = node.Qemu.OrderBy(g => g.VmId).ToList(),
        Storage = node.Storage
            .OrderBy(s => s.Storage, StringComparer.Ordinal)
            .Select(NormalizeStorage)
            .ToList(),
        Network = node.Network
            .OrderBy(n => n.Iface, StringComparer.Ordinal)
            .ToList(),
    };

    private static StorageSnapshot NormalizeStorage(StorageSnapshot storage) => storage with
    {
        Content = SortTokens(storage.Content),
    };

    // Proxmox reports storage `content` as a comma-joined set in arbitrary order
    // (e.g. "vztmpl,backup,images" vs "backup,images,vztmpl"). Sort the tokens so
    // an unchanged content set always serializes identically.
    private static string? SortTokens(string? csv)
    {
        if (string.IsNullOrEmpty(csv)) return csv;
        return string.Join(",", csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(t => t, StringComparer.Ordinal));
    }
}
