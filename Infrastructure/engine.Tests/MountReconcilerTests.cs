using System.Linq;
using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// MountReconciler (#13) — pure + mocked, no live cluster.
public sealed class MountReconcilerTests
{
    private static Shape SonarrLike()
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "sonarr" } };
        s.Spec.Node = "hpe-01";
        s.Spec.Ctid = "5101";
        s.Spec.Mounts.Add(new MountSpec
        {
            Type = "nfs", Storage = "ds1813-nfs-volume-4", Source = "data",
            Target = "/data", Acl = true, Backup = false,
        });
        s.Spec.Hookscript = "local:snippets/ensure-data-mount.sh";
        return s;
    }

    [Fact]
    public void RenderMount_NfsPathBind_RendersUnderStorageHostMount()
    {
        var v = MountReconciler.RenderMount(new MountSpec
        {
            Type = "nfs", Storage = "ds1813-nfs-volume-4", Source = "data",
            Target = "/data", Acl = true, Backup = false,
        });
        Assert.Equal("/mnt/pve/ds1813-nfs-volume-4/data,mp=/data,backup=0,acl=1", v);
    }

    [Fact]
    public void RenderMount_AllocatedVolumeOrDevice_ReturnsNull()
    {
        // nfs with size (allocated volume) — no source → not yet rendered
        Assert.Null(MountReconciler.RenderMount(new MountSpec { Type = "nfs", Storage = "s", Size = "50G", Target = "/d" }));
        Assert.Null(MountReconciler.RenderMount(new MountSpec { Type = "volume", Storage = "s", Target = "/d" }));
        Assert.Null(MountReconciler.RenderMount(new MountSpec { Type = "device", Source = "/dev/x", Target = "/d" }));
    }

    [Fact]
    public void MountMatches_IgnoresOptionOrderAndUnmanagedOptions()
    {
        const string desired = "/mnt/pve/v4/data,mp=/data,backup=0,acl=1";
        Assert.True(MountReconciler.MountMatches("/mnt/pve/v4/data,mp=/data,acl=1,replicate=0,backup=0", desired));
        Assert.False(MountReconciler.MountMatches("/mnt/pve/OTHER/data,mp=/data,backup=0,acl=1", desired)); // volume differs
        Assert.False(MountReconciler.MountMatches("/mnt/pve/v4/data,mp=/data,acl=1", desired));             // backup=0 missing
    }

    [Fact]
    public async Task Reconciler_NoChange_WhenMountAndHookscriptMatch()
    {
        var exec = new FakeExec(cmd => cmd.Contains("pct config")
            ? new ExecResult(0, "cores: 2\nmp0: /mnt/pve/ds1813-nfs-volume-4/data,mp=/data,backup=0,acl=1\nhookscript: local:snippets/ensure-data-mount.sh", "")
            : throw new InvalidOperationException($"unexpected mutating command: {cmd}"));

        var r = await new MountReconciler(exec).ReconcileAsync(SonarrLike());

        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
    }

    [Fact]
    public async Task Reconciler_AppliesMountAndHookscript_WhenAbsent()
    {
        var exec = new FakeExec(cmd => cmd.Contains("pct config")
            ? new ExecResult(0, "cores: 2\nmemory: 1024", "") // no mp0, no hookscript
            : new ExecResult(0, "", ""));

        var r = await new MountReconciler(exec).ReconcileAsync(SonarrLike());

        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        var set = exec.Commands.Single(c => c.Contains("pct set"));
        Assert.Contains("--mp0 /mnt/pve/ds1813-nfs-volume-4/data,mp=/data,backup=0,acl=1", set);
        Assert.Contains("--hookscript local:snippets/ensure-data-mount.sh", set);
    }

    [Fact]
    public async Task Reconciler_EnsuresPathBindSourceDir_GuardedAndBeforePctSet()
    {
        var exec = new FakeExec(cmd => cmd.Contains("pct config")
            ? new ExecResult(0, "cores: 2", "")   // no mp0 → mount will be applied
            : new ExecResult(0, "", ""));

        var r = await new MountReconciler(exec).ReconcileAsync(SonarrLike());

        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        var mk = exec.Commands.Single(c => c.Contains("mkdir -p"));
        Assert.Contains("mountpoint -q /mnt/pve/ds1813-nfs-volume-4", mk);   // guarded: only on a mounted export
        Assert.Contains("mkdir -p /mnt/pve/ds1813-nfs-volume-4/data", mk);
        Assert.True(exec.Commands.FindIndex(c => c.Contains("mkdir -p"))
                  < exec.Commands.FindIndex(c => c.Contains("pct set")));   // dir exists before the bind
    }

    [Fact]
    public async Task Reconciler_SkipsAllocatedVolumeMount()
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "forgejo" } };
        s.Spec.Node = "desktop-01";
        s.Spec.Ctid = "3000";
        s.Spec.Mounts.Add(new MountSpec { Type = "nfs", Storage = "ds1813-nfs-volume-3", Size = "50G", Target = "/mnt/forgejo-data" });

        var exec = new FakeExec(cmd => cmd.Contains("pct config")
            ? new ExecResult(0, "cores: 2", "")
            : throw new InvalidOperationException($"must not mutate: {cmd}"));

        var r = await new MountReconciler(exec).ReconcileAsync(s);

        Assert.Equal(ApplyOutcome.Skipped, r.Outcome);
    }

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
