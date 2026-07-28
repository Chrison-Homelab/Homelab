using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Apply-time behaviour of `--only` (issue #306), driven through ConvergeRunner over a
// throwaway stack directory and a fake exec. No cluster, no SSH.
//
// The unit under scrutiny is the dependency guard: a dependency left OUT of the
// selection is being assumed already converged, and the runner has to verify that
// rather than trust it. Getting this wrong either blocks legitimate scoped applies or
// lets one run half-applied against a missing dependency.
public sealed class ConvergeOnlyApplyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hl306-" + Guid.NewGuid().ToString("n")[..12]);

    public ConvergeOnlyApplyTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "stack.yaml"), """
            apiVersion: homelab/v1
            kind: Stack
            metadata:
              name: TestStack
            spec:
              defaults:
                node: testnode
            """);
        // `child` dependsOn `base`; only `child` will ever be selected.
        File.WriteAllText(Path.Combine(_dir, "base.lxc.yaml"), """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: base
            spec:
              app: mqtt
              ctid: 9001
            """);
        File.WriteAllText(Path.Combine(_dir, "child.lxc.yaml"), """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: child
              stack: TestStack
            spec:
              app: mqtt
              ctid: 9002
              dependsOn: [base]
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // `pct status <ctid>` is how existence is probed; a non-zero exit means absent.
    private sealed class FakeExec : INodeExec
    {
        private readonly HashSet<string> _absent;
        public List<string> Commands { get; } = new();
        public FakeExec(params string[] absentCtids) => _absent = new HashSet<string>(absentCtids, StringComparer.Ordinal);

        public Task<ExecResult> OnNodeAsync(string node, string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (command.StartsWith("pct status ", StringComparison.Ordinal))
            {
                var ctid = command["pct status ".Length..].Trim();
                return Task.FromResult(_absent.Contains(ctid)
                    ? new ExecResult(2, "", $"CT {ctid} does not exist")
                    : new ExecResult(0, "status: running", ""));
            }
            return Task.FromResult(new ExecResult(0, "", ""));
        }

        public Task<ExecResult> InContainerAsync(string node, string ctid, string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(new ExecResult(0, "", ""));
        }
    }

    private Task<int> ApplyAsync(FakeExec exec, params string[] only) =>
        new ConvergeRunner(_dir, SecretsEnv.Load(null), exec: exec, only: only).ApplyAsync();

    [Fact]
    public async Task Only_FailsWhenAnUnselectedDependencyIsNotConverged()
    {
        // base (9002's dependency) is absent → applying child alone must refuse.
        var exec = new FakeExec(absentCtids: "9001");
        var rc = await ApplyAsync(exec, "child");

        Assert.NotEqual(0, rc);
        // And it must refuse BEFORE doing any work on the selected member. The guard
        // runs ahead of the existence probe, so child's own `pct status` never fires —
        // that absence is the proof it bailed early rather than part-way through.
        Assert.DoesNotContain(exec.Commands, c => c == "pct status 9002");
        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_ProceedsWhenTheUnselectedDependencyIsAlreadyConverged()
    {
        // Both exist → the normal scoped-apply case. This is the one that must NOT
        // regress: failing here would make --only useless for any member with a dep.
        var exec = new FakeExec(); // nothing absent
        var rc = await ApplyAsync(exec, "child");

        Assert.Equal(0, rc);
        Assert.Contains(exec.Commands, c => c == "pct status 9002");
    }

    [Fact]
    public async Task Only_DoesNotTouchUnselectedMembers()
    {
        // The whole point of #306: the unselected member is never probed or written.
        var exec = new FakeExec();
        await ApplyAsync(exec, "child");

        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct set 9001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_SelectingBothSkipsTheDependencyGuardEntirely()
    {
        // With `base` inside the selection there is no outside-dependency to assert,
        // so even an absent base must not trip the guard on `child` — base is being
        // converged in this very run.
        var exec = new FakeExec(absentCtids: "9001");
        await ApplyAsync(exec, "base", "child");

        // child reached its own existence probe, i.e. it was processed rather than
        // failed up-front for a dependency that IS in the selection.
        Assert.Contains(exec.Commands, c => c == "pct status 9002");
    }

    [Fact]
    public async Task UnknownMember_ThrowsBeforeAnythingRuns()
    {
        var exec = new FakeExec();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(exec, "nosuch"));
        Assert.Empty(exec.Commands);
    }
}
