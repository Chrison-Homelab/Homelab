using ProxmoxSharp;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// The drift PR body: a Markdown table of what changed between the committed
// snapshot and a fresh discovery.
public sealed class DriftSummaryTests
{
    private static GuestSnapshot Guest(long id, string name, string status = "running", int cores = 1) =>
        new() { VmId = id, Name = name, Status = status, MaxMem = 1L << 30, Cores = cores };

    private static ClusterSnapshot One(string node, params GuestSnapshot[] lxc) =>
        new() { Nodes = [new NodeSnapshot { Node = node, Status = "online", Lxc = lxc }] };

    [Fact]
    public void Identical_snapshots_report_no_drift()
    {
        var snap = One("hpe-01", Guest(5002, "prowlarr"));
        var md = DriftSummary.Render(snap, snap);
        Assert.Contains("No drift", md);
        Assert.DoesNotContain("| Change |", md);
    }

    [Fact]
    public void Added_changed_and_removed_each_render_a_row()
    {
        var before = One("hpe-01",
            Guest(5002, "prowlarr"),
            Guest(5013, "tracearr", status: "running"));
        var after = One("hpe-01",
            Guest(5013, "tracearr", status: "stopped"), // changed
            Guest(2010, "cloudflared-hpe-01"));          // added; 5002 removed

        var md = DriftSummary.Render(before, after);

        Assert.Contains("| Change | Node | Resource | Detail |", md);
        // added
        Assert.Contains("added", md);
        Assert.Contains("lxc 2010 cloudflared-hpe-01", md);
        // changed shows the field transition
        Assert.Contains("lxc 5013 tracearr", md);
        Assert.Contains("status running→stopped", md);
        // removed
        Assert.Contains("removed", md);
        Assert.Contains("lxc 5002 prowlarr", md);
        Assert.Contains("gone", md);
        // 3 data rows + header + separator
        Assert.Equal(3, md.Split('\n').Count(l => l.StartsWith("| ") && !l.Contains("Change") && !l.Contains("---")));
    }

    [Fact]
    public void Storage_content_reorder_is_not_drift()
    {
        // Same content set, DIFFERENT token order — must be treated as equal.
        var before = new ClusterSnapshot
        {
            Nodes = [new NodeSnapshot { Node = "hpe-01",
                Storage = [new StorageSnapshot { Storage = "local", Type = "dir", Content = "iso,vztmpl,backup" }] }],
        };
        var after = new ClusterSnapshot
        {
            Nodes = [new NodeSnapshot { Node = "hpe-01",
                Storage = [new StorageSnapshot { Storage = "local", Type = "dir", Content = "backup,iso,vztmpl" }] }],
        };

        Assert.Contains("No drift", DriftSummary.Render(before, after));
    }

    [Fact]
    public void Memory_change_renders_in_binary_units()
    {
        var before = One("hpe-01", new GuestSnapshot { VmId = 5004, Name = "radarr", Status = "running", MaxMem = 1L << 30, Cores = 1 });
        var after = One("hpe-01", new GuestSnapshot { VmId = 5004, Name = "radarr", Status = "running", MaxMem = 2L << 30, Cores = 1 });

        var md = DriftSummary.Render(before, after);
        Assert.Contains("mem 1G→2G", md);
    }
}
