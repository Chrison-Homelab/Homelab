using PowerOrchestrator.Core.Model;
using PowerOrchestrator.Core.Policy;
using Xunit;

namespace PowerOrchestrator.Tests;

public sealed class ArmGuardTests
{
    // The homelab's actual corosync.conf shape: three nodes, one vote each, no QDevice.
    private const string ThreeNodes = """
        nodelist {
          node {
            name: desktop-01
            nodeid: 2
            quorum_votes: 1
            ring0_addr: 10.0.0.12
          }
          node {
            name: hpe-01
            nodeid: 3
            quorum_votes: 1
            ring0_addr: 10.0.0.13
          }
          node {
            name: nuc-01
            nodeid: 1
            quorum_votes: 1
            ring0_addr: 10.0.0.11
          }
        }

        quorum {
          provider: corosync_votequorum
        }
        """;

    private static NodeState Idle(string n) => new(n, IsOnline: true, RunningGuests: 0);
    private static NodeState Busy(string n, int guests) => new(n, IsOnline: true, RunningGuests: guests);

    [Fact]
    public void Before_first_poll_nothing_is_met()
    {
        Assert.False(ArmGuard.CanArm(ArmGuard.Unknown(), out var unmet));
        Assert.Equal(2, unmet.Count);
        Assert.All(unmet, p => Assert.False(string.IsNullOrWhiteSpace(p.Detail)));
    }

    [Fact]
    public void Running_guests_on_a_managed_node_block_arming_with_actionable_detail()
    {
        // desktop-01 as it was: eight always-on containers.
        var pre = ArmGuard.Evaluate([Busy("desktop-01", 8)], ["desktop-01"], ThreeNodes);

        Assert.False(ArmGuard.CanArm(pre, out var unmet));
        var evac = Assert.Single(unmet);
        Assert.Equal(ArmGuard.Evacuated, evac.Name);
        Assert.Contains("desktop-01=8", evac.Detail);
        Assert.Contains("onboot=0", evac.Detail);
    }

    [Fact]
    public void Idle_managed_node_in_a_three_node_cluster_can_arm()
    {
        // The target state: nothing runs on desktop-01, and nuc-01 + hpe-01 keep 2 of 3 votes.
        var pre = ArmGuard.Evaluate([Idle("desktop-01")], ["desktop-01"], ThreeNodes);

        Assert.True(ArmGuard.CanArm(pre, out _));
        Assert.Contains(pre, p => p.Name == ArmGuard.Quorum && p.Detail.Contains("2 of 3"));
    }

    [Fact]
    public void Asleep_managed_node_counts_as_evacuated()
    {
        // Offline = nothing running; the policy must not report a sleeping node as a blocker.
        var pre = ArmGuard.Evaluate([new NodeState("desktop-01", IsOnline: false, RunningGuests: 0)], ["desktop-01"], ThreeNodes);
        Assert.True(pre.Single(p => p.Name == ArmGuard.Evacuated).Met);
    }

    [Fact]
    public void Two_sleepers_of_four_need_a_qdevice()
    {
        var four = ThreeNodes.Replace("quorum {", "  node {\n    name: hpe-02\n    nodeid: 4\n    quorum_votes: 1\n    ring0_addr: 10.0.0.14\n  }\n}\nquorum {").Replace("}\n\n  node {\n    name: hpe-02", "  node {\n    name: hpe-02");
        var (met, detail) = CorosyncQuorum.Evaluate(four, ["desktop-01", "hpe-02"]);
        Assert.False(met);
        Assert.Contains("2 of 4", detail);

        var withDevice = four.Replace("provider: corosync_votequorum", "provider: corosync_votequorum\n  device {\n    model: net\n  }");
        Assert.True(CorosyncQuorum.Evaluate(withDevice, ["desktop-01", "hpe-02"]).Met);
    }

    [Fact]
    public void Unreadable_corosync_conf_is_unmet_not_assumed()
    {
        var (met, detail) = CorosyncQuorum.Evaluate(null, ["desktop-01"]);
        Assert.False(met);
        Assert.Contains("not readable", detail);
    }
}
