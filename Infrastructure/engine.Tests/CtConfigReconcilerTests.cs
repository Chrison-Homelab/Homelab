using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// CtConfigReconciler resource reconciliation (#478): swap is set alongside cores/memory,
// and rootfs GROWS via `pct resize` while a SHRINK is refused. Pure + mocked, no cluster.
public sealed class CtConfigReconcilerTests
{
    private static Shape Ct(string ctid = "3003", int? cores = null, int? memory = null,
        int? swap = null, int? disk = null)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "shell" } };
        s.Spec.Node = "desktop-01";
        s.Spec.Ctid = ctid;
        s.Spec.Cores = cores;
        s.Spec.Memory = memory;
        s.Spec.Swap = swap;
        s.Spec.Disk = disk;
        return s;
    }

    // pct config reply with the given live values; rootfs carries a size=NNG like Proxmox.
    private static FakeExec ExecWith(int cores, int memory, int swap, int rootfsGb)
    {
        var cfg = $"cores: {cores}\nmemory: {memory}\nswap: {swap}\n" +
                  $"rootfs: local-lvm:vm-3003-disk-0,size={rootfsGb}G\n" +
                  "net0: name=eth0,bridge=vmbr0,hwaddr=AA:BB:CC:DD:EE:FF,ip=dhcp,tag=1010,type=veth";
        return new FakeExec(cmd => cmd.StartsWith("pct config", StringComparison.Ordinal)
            ? new ExecResult(0, cfg, "")
            : new ExecResult(0, "", ""));
    }

    [Fact]
    public async Task Swap_IsSet_WhenDeclaredAndDiffers()
    {
        var exec = ExecWith(cores: 4, memory: 6144, swap: 512, rootfsGb: 32);
        var r = await new CtConfigReconciler(exec).ReconcileAsync(Ct(swap: 2048));
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        var set = exec.Commands.Single(c => c.StartsWith("pct set", StringComparison.Ordinal));
        Assert.Contains("--swap 2048", set);
    }

    [Fact]
    public async Task Swap_NoChange_WhenMatches()
    {
        var exec = ExecWith(cores: 4, memory: 6144, swap: 512, rootfsGb: 32);
        var r = await new CtConfigReconciler(exec).ReconcileAsync(Ct(swap: 512));
        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
        Assert.DoesNotContain(exec.Commands, c => c.StartsWith("pct set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disk_Grows_ViaResizeWithPositiveDelta()
    {
        var exec = ExecWith(cores: 4, memory: 6144, swap: 512, rootfsGb: 32);
        var r = await new CtConfigReconciler(exec).ReconcileAsync(Ct(disk: 64));
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        // Delta form, not absolute: an absolute target is a Proxmox no-op.
        Assert.Contains(exec.Commands, c => c == "pct resize 3003 rootfs +32G");
        Assert.Contains("disk 32G→64G", r.Message);
    }

    [Fact]
    public async Task Disk_NoChange_WhenSizeMatches()
    {
        var exec = ExecWith(cores: 4, memory: 6144, swap: 512, rootfsGb: 32);
        var r = await new CtConfigReconciler(exec).ReconcileAsync(Ct(disk: 32));
        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct resize"));
    }

    [Fact]
    public async Task Disk_Shrink_IsRefused_NotAttempted()
    {
        var exec = ExecWith(cores: 4, memory: 6144, swap: 512, rootfsGb: 32);
        var r = await new CtConfigReconciler(exec).ReconcileAsync(Ct(disk: 16));
        // Skipped (not Failed) so the member's remaining steps still run.
        Assert.Equal(ApplyOutcome.Skipped, r.Outcome);
        Assert.Contains("SHRINK REFUSED", r.Message);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct resize"));
    }

    [Fact]
    public async Task Grow_AndSet_BothApplied_InOnePass()
    {
        var exec = ExecWith(cores: 4, memory: 6144, swap: 512, rootfsGb: 32);
        var r = await new CtConfigReconciler(exec).ReconcileAsync(Ct(cores: 8, memory: 8192, swap: 2048, disk: 64));
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        var set = exec.Commands.Single(c => c.StartsWith("pct set", StringComparison.Ordinal));
        Assert.Contains("--cores 8", set);
        Assert.Contains("--memory 8192", set);
        Assert.Contains("--swap 2048", set);
        Assert.Contains(exec.Commands, c => c == "pct resize 3003 rootfs +32G");
    }

    [Fact]
    public async Task Shrink_RidesAlong_WhenOtherFieldsAlsoChange()
    {
        var exec = ExecWith(cores: 4, memory: 6144, swap: 512, rootfsGb: 32);
        var r = await new CtConfigReconciler(exec).ReconcileAsync(Ct(cores: 8, disk: 16));
        // A real write happened (cores), so Applied — but the refusal must still be reported.
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.Contains("SHRINK REFUSED", r.Message);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct resize"));
    }

    [Theory]
    [InlineData("local-lvm:vm-3003-disk-0,size=32G", 32)]
    [InlineData("local-lvm:subvol-100-disk-0,size=8G", 8)]
    [InlineData("nas:100/vm-100-disk-0.raw,size=300G,backup=0", 300)]
    public void ParseRootfsSizeGb_ReadsGigabytes(string rootfs, int expected)
        => Assert.Equal(expected, CtConfigReconciler.ParseRootfsSizeGb(rootfs));

    [Theory]
    [InlineData("local-lvm:vm-3003-disk-0")]        // no size=
    [InlineData("local-lvm:vm-3003-disk-0,size=512M")] // not in G — refuse to guess
    [InlineData(null)]
    [InlineData("")]
    public void ParseRootfsSizeGb_NullWhenAbsentOrNotGigabytes(string? rootfs)
        => Assert.Null(CtConfigReconciler.ParseRootfsSizeGb(rootfs));

    private sealed class FakeExec : INodeExec
    {
        private readonly Func<string, ExecResult> _reply;
        public List<string> Commands { get; } = new();
        public FakeExec(Func<string, ExecResult> reply) => _reply = reply;

        public Task<ExecResult> OnNodeAsync(string node, string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(_reply(command));
        }

        public Task<ExecResult> InContainerAsync(string node, string ctid, string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(_reply(command));
        }
    }
}
