using System.Linq;
using System.Threading.Tasks;
using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Multi-NIC LXC support (#383) — pure + mocked, no live cluster.
public sealed class LxcNetTests
{
    private static NetworkInterfaceSpec Homelab() => new()
    {
        Bridge = "vmbr0", Tag = 1010, Ip = "dhcp", Firewall = true,
        Hwaddr = "bc:24:11:3c:03:54",
    };

    private static NetworkInterfaceSpec IotLeg() => new()
    {
        Bridge = "vmbr0", Tag = 1040, Ip = "dhcp", Firewall = true,
    };

    [Fact]
    public void Render_EmitsLxcShape_NotQemuShape()
    {
        // LXC needs name= and type=veth, and carries the MAC on its own hwaddr key.
        var v = LxcNet.Render(Homelab(), 0);
        Assert.Equal(
            "name=vlan1010,bridge=vmbr0,hwaddr=BC:24:11:3C:03:54,ip=dhcp,tag=1010,firewall=1,type=veth", v);
    }

    [Fact]
    public void Render_DefaultsInterfaceNameToVlanTag_MatchingTheCreatePath()
    {
        // community-scripts names the created net0 `vlan<tag>`. Defaulting to eth0 here
        // would make the first reconcile rename net0 and bounce the primary link.
        Assert.Equal("vlan1040", LxcNet.InterfaceName(IotLeg(), 1));
        Assert.Equal("eth1", LxcNet.InterfaceName(new NetworkInterfaceSpec { Bridge = "vmbr0" }, 1));
        Assert.Equal("custom", LxcNet.InterfaceName(new NetworkInterfaceSpec { Name = "custom", Tag = 7 }, 0));
    }

    [Fact]
    public void Render_UnpinnedMac_CarriesTheLiveOneForward()
    {
        // Re-issuing `pct set --netN` without hwaddr makes Proxmox mint a NEW MAC, which
        // silently invalidates the DHCP reservation (and the DNS record keyed on it).
        var v = LxcNet.Render(IotLeg(), 1, liveHwaddr: "BC:24:11:C0:7F:BB");
        Assert.Contains("hwaddr=BC:24:11:C0:7F:BB", v);

        // A shape that DOES pin one wins over the live value — that's a deliberate change.
        var pinned = LxcNet.Render(Homelab(), 0, liveHwaddr: "AA:BB:CC:DD:EE:FF");
        Assert.Contains("hwaddr=BC:24:11:3C:03:54", pinned);
    }

    [Fact]
    public void Render_LinkDownOnlyWhenTrue()
    {
        Assert.Contains("link_down=1", LxcNet.Render(new NetworkInterfaceSpec { Bridge = "vmbr0", LinkDown = true }, 0));
        Assert.DoesNotContain("link_down", LxcNet.Render(new NetworkInterfaceSpec { Bridge = "vmbr0", LinkDown = false }, 0));
        Assert.DoesNotContain("link_down", LxcNet.Render(new NetworkInterfaceSpec { Bridge = "vmbr0" }, 0));
    }

    [Fact]
    public void Matches_IgnoresKeyOrderAndUnmanagedKeys()
    {
        var desired = LxcNet.Render(Homelab(), 0);

        // Proxmox echoes keys in its own order and adds ones we never set.
        Assert.True(LxcNet.Matches(
            "name=vlan1010,type=veth,hwaddr=BC:24:11:3C:03:54,bridge=vmbr0,firewall=1,ip=dhcp,tag=1010", desired));

        // A declared key that differs IS drift.
        Assert.False(LxcNet.Matches(
            "name=vlan1010,bridge=vmbr0,hwaddr=BC:24:11:3C:03:54,ip=dhcp,tag=1040,firewall=1,type=veth", desired));

        // A declared key that's missing live is drift too.
        Assert.False(LxcNet.Matches("name=vlan1010,bridge=vmbr0,ip=dhcp,type=veth", desired));
        Assert.False(LxcNet.Matches(null, desired));
    }

    [Fact]
    public void Matches_MacComparesCaseInsensitively()
    {
        var desired = LxcNet.Render(Homelab(), 0);
        Assert.True(LxcNet.Matches(
            "name=vlan1010,bridge=vmbr0,hwaddr=bc:24:11:3c:03:54,ip=dhcp,tag=1010,firewall=1,type=veth", desired));
    }

    // ---- reconciler wiring -------------------------------------------------

    private static Shape DualHomed()
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "homeassistant" } };
        s.Spec.Node = "hpe-01";
        s.Spec.Ctid = "6005";
        s.Spec.Networks.Add(Homelab());
        s.Spec.Networks.Add(IotLeg());
        return s;
    }

    [Fact]
    public async Task Reconciler_AddsTheMissingSecondNic()
    {
        var exec = new FakeExec(
            "cores: 4\nmemory: 4096\n" +
            "net0: name=vlan1010,bridge=vmbr0,hwaddr=BC:24:11:3C:03:54,ip=dhcp,tag=1010,firewall=1,type=veth\n");

        var r = await new CtConfigReconciler(exec).ReconcileAsync(DualHomed());

        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        var set = exec.Commands.Last();
        Assert.Contains("--net1", set);
        Assert.Contains("name=vlan1040", set);
        Assert.Contains("tag=1040", set);
        Assert.DoesNotContain("--net0", set);   // net0 already matched — untouched
    }

    [Fact]
    public async Task Reconciler_NoChange_WhenBothNicsAlreadyMatch()
    {
        var exec = new FakeExec(
            "net0: name=vlan1010,bridge=vmbr0,hwaddr=BC:24:11:3C:03:54,ip=dhcp,tag=1010,firewall=1,type=veth\n" +
            "net1: name=vlan1040,bridge=vmbr0,hwaddr=BC:24:11:C0:7F:BB,ip=dhcp,tag=1040,firewall=1,type=veth\n");

        var r = await new CtConfigReconciler(exec).ReconcileAsync(DualHomed());

        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
        Assert.Single(exec.Commands);   // only the `pct config` read
    }

    [Fact]
    public async Task Reconciler_NeverDeletesANicTheShapeDoesNotDeclare()
    {
        // A third live NIC we don't own must survive untouched.
        var exec = new FakeExec(
            "net0: name=vlan1010,bridge=vmbr0,hwaddr=BC:24:11:3C:03:54,ip=dhcp,tag=1010,firewall=1,type=veth\n" +
            "net1: name=vlan1040,bridge=vmbr0,hwaddr=BC:24:11:C0:7F:BB,ip=dhcp,tag=1040,firewall=1,type=veth\n" +
            "net2: name=eth2,bridge=vmbr1,hwaddr=AA:BB:CC:DD:EE:FF,ip=dhcp,type=veth\n");

        var r = await new CtConfigReconciler(exec).ReconcileAsync(DualHomed());

        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("net2"));
    }

    [Fact]
    public async Task Reconciler_CorrectsADriftedNicButKeepsItsGeneratedMac()
    {
        // net1 drifted onto the wrong VLAN; the shape doesn't pin its MAC, so the rewrite
        // must carry the existing one forward or the DHCP reservation breaks.
        var exec = new FakeExec(
            "net0: name=vlan1010,bridge=vmbr0,hwaddr=BC:24:11:3C:03:54,ip=dhcp,tag=1010,firewall=1,type=veth\n" +
            "net1: name=vlan1040,bridge=vmbr0,hwaddr=BC:24:11:C0:7F:BB,ip=dhcp,tag=999,firewall=1,type=veth\n");

        var r = await new CtConfigReconciler(exec).ReconcileAsync(DualHomed());

        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        var set = exec.Commands.Last();
        Assert.Contains("--net1", set);
        Assert.Contains("tag=1040", set);
        Assert.Contains("hwaddr=BC:24:11:C0:7F:BB", set);
    }

    [Fact]
    public void LoadStack_MemberWithNetworks_DoesNotAlsoInheritTheNetworkSugar()
    {
        // The schema forbids both in one file, but it can't see the defaults merge. Every
        // stack here sets `network` in defaults, so without the gate a multi-NIC member
        // would end up describing net0 twice.
        var dir = Path.Combine(Path.GetTempPath(), "lxcnet-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "stack.yaml"), """
                apiVersion: homelab/v1
                kind: Stack
                metadata: { name: SmartHome }
                spec:
                  ctidRange: { start: 6000, end: 6099 }
                  defaults:
                    node: hpe-01
                    network: { bridge: vmbr0, vlan: 1040, ipv4: dhcp }
                """);
            File.WriteAllText(Path.Combine(dir, "multi.lxc.yaml"), """
                apiVersion: homelab/v1
                kind: LXC
                metadata: { name: multi }
                spec:
                  app: docker
                  ctid: 6005
                  networks:
                    - { bridge: vmbr0, tag: 1010, ip: dhcp }
                    - { bridge: vmbr0, tag: 1040, ip: dhcp }
                """);
            File.WriteAllText(Path.Combine(dir, "single.lxc.yaml"), """
                apiVersion: homelab/v1
                kind: LXC
                metadata: { name: single }
                spec:
                  app: docker
                  ctid: 6006
                """);

            var loaded = ShapeLoader.LoadStack(dir);

            var multi = loaded.Members.Single(m => m.Metadata.Name == "multi");
            Assert.Null(multi.Spec.Network);            // sugar NOT inherited
            Assert.Equal(2, multi.Spec.Networks.Count);

            // A member without its own list still inherits the sugar as before.
            var single = loaded.Members.Single(m => m.Metadata.Name == "single");
            Assert.NotNull(single.Spec.Network);
            Assert.Equal(1040, single.Spec.Network!.Vlan);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class FakeExec : INodeExec
    {
        private readonly string _config;
        public List<string> Commands { get; } = new();
        public FakeExec(string config) => _config = config;

        public Task<ExecResult> OnNodeAsync(string node, string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(command.StartsWith("pct config", StringComparison.Ordinal)
                ? new ExecResult(0, _config, "")
                : new ExecResult(0, "", ""));
        }

        public Task<ExecResult> InContainerAsync(string node, string ctid, string command, CancellationToken ct = default)
            => Task.FromResult(new ExecResult(0, "", ""));
    }
}
