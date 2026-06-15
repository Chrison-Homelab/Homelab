using System.Text.Json;
using System.Text.Json.Serialization;
using ProxmoxSharp;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Discover output must be deterministic: re-running against an unchanged cluster
// has to produce byte-identical JSON, or the drift workflow opens a churn PR on
// every run. Proxmox returns collections in arbitrary order + a volatile uptime.
public sealed class SnapshotNormalizerTests
{
    // The same JSON options the `discover` command serializes with.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static GuestSnapshot Guest(long id, string name) =>
        new() { VmId = id, Name = name, Status = "running", MaxMem = 1L << 30, Cores = 1 };

    [Fact]
    public void Normalize_sorts_all_collections_and_drops_uptime()
    {
        var snapshot = new ClusterSnapshot
        {
            Nodes =
            [
                new NodeSnapshot
                {
                    Node = "hpe-01",
                    Status = "online",
                    MaxMem = 1L << 36,
                    Uptime = 1697102, // volatile — must be dropped
                    Lxc = [Guest(5012, "romm"), Guest(5002, "prowlarr"), Guest(5004, "radarr")],
                    Qemu = [Guest(1002, "windows"), Guest(1001, "bazzite")],
                    Storage =
                    [
                        new StorageSnapshot { Storage = "local-lvm", Type = "lvmthin", Content = "images,rootdir" },
                        new StorageSnapshot { Storage = "ds1813-nfs-volume-1", Type = "nfs", Content = "vztmpl,backup,iso" },
                    ],
                    Network =
                    [
                        new NetworkSnapshot { Iface = "vmbr0", Type = "Bridge" },
                        new NetworkSnapshot { Iface = "eno1", Type = "Eth" },
                    ],
                },
                new NodeSnapshot { Node = "desktop-01", Status = "online" },
            ],
        };

        var result = SnapshotNormalizer.Normalize(snapshot);

        Assert.Equal(["desktop-01", "hpe-01"], result.Nodes.Select(n => n.Node));

        var hpe = result.Nodes.Single(n => n.Node == "hpe-01");
        Assert.Null(hpe.Uptime);
        Assert.Equal([5002, 5004, 5012], hpe.Lxc.Select(g => g.VmId));
        Assert.Equal([1001, 1002], hpe.Qemu.Select(g => g.VmId));
        Assert.Equal(["ds1813-nfs-volume-1", "local-lvm"], hpe.Storage.Select(s => s.Storage));
        Assert.Equal(["eno1", "vmbr0"], hpe.Network.Select(n => n.Iface));
        // content tokens sorted within the field
        Assert.Equal("backup,iso,vztmpl", hpe.Storage.Single(s => s.Storage == "ds1813-nfs-volume-1").Content);
    }

    [Fact]
    public void Normalize_yields_identical_json_regardless_of_input_order_or_uptime()
    {
        // Same cluster, reported in two different orderings with a changed uptime —
        // exactly the two real `discover` runs that produced a spurious diff.
        var runA = new ClusterSnapshot
        {
            Nodes =
            [
                new NodeSnapshot
                {
                    Node = "hpe-01",
                    Uptime = 1697102,
                    Lxc = [Guest(5004, "radarr"), Guest(5002, "prowlarr")],
                    Storage = [new StorageSnapshot { Storage = "local", Content = "iso,vztmpl,backup" }],
                },
            ],
        };
        var runB = new ClusterSnapshot
        {
            Nodes =
            [
                new NodeSnapshot
                {
                    Node = "hpe-01",
                    Uptime = 1699732, // later read
                    Lxc = [Guest(5002, "prowlarr"), Guest(5004, "radarr")],
                    Storage = [new StorageSnapshot { Storage = "local", Content = "backup,iso,vztmpl" }],
                },
            ],
        };

        var jsonA = JsonSerializer.Serialize(SnapshotNormalizer.Normalize(runA), JsonOptions);
        var jsonB = JsonSerializer.Serialize(SnapshotNormalizer.Normalize(runB), JsonOptions);

        Assert.Equal(jsonA, jsonB);
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var snapshot = new ClusterSnapshot
        {
            Nodes = [new NodeSnapshot { Node = "n1", Lxc = [Guest(3, "c"), Guest(1, "a")] }],
        };

        var once = JsonSerializer.Serialize(SnapshotNormalizer.Normalize(snapshot), JsonOptions);
        var twice = JsonSerializer.Serialize(SnapshotNormalizer.Normalize(SnapshotNormalizer.Normalize(snapshot)), JsonOptions);

        Assert.Equal(once, twice);
    }
}
