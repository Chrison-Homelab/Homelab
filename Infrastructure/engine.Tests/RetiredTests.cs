using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// `spec.manage: retired` (issue #362). Retirement used to be a `retired` TAG plus a comment.
// The engine reads neither, so a retired member whose CT had been deleted was indistinguishable
// from one that had never been created — converge saw CREATE and made a new one. A Media deploy
// rebuilt CT 5113 (youtarr) months after the podman host replaced it.
//
// The absent case is the one that matters here, and it is the opposite of DescribeOnlyTests:
// there the guest exists and must not be touched; here it is GONE and must stay gone.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class RetiredTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hl362-" + Guid.NewGuid().ToString("n")[..12]);

    public RetiredTests()
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
        // Deliberately absent from FakeState below — the state that caused #362.
        File.WriteAllText(Path.Combine(_dir, "gone.lxc.yaml"), """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: gone
            spec:
              app: mqtt
              ctid: 9201
              cores: 2
              manage: retired
            """);
        File.WriteAllText(Path.Combine(_dir, "normal.lxc.yaml"), """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: normal
            spec:
              app: mqtt
              ctid: 9202
              cores: 2
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class FakeExec : INodeExec
    {
        public List<string> Commands { get; } = new();
        public Task<ExecResult> OnNodeAsync(string node, string cmd, CancellationToken ct = default)
        {
            Commands.Add(cmd);
            return Task.FromResult(cmd.StartsWith("pct status ", StringComparison.Ordinal)
                ? new ExecResult(0, "status: running", "")
                : new ExecResult(0, "", ""));
        }
        public Task<ExecResult> InContainerAsync(string node, string ctid, string cmd, CancellationToken ct = default)
        { Commands.Add(cmd); return Task.FromResult(new ExecResult(0, "", "")); }
    }

    // Only the managed member exists live. The retired one is absent — as it should be.
    private sealed class FakeState : IClusterStateProvider
    {
        public Task<ClusterState?> TryGetAsync(CancellationToken ct = default) =>
            Task.FromResult<ClusterState?>(new ClusterState(new[]
            {
                new LiveCt(9202, "testnode", "normal", "running", 1024L * 1024 * 1024, Cores: 8, Tags: ""),
            }));
    }

    // The retired guest is still present — the state after #362 already fired.
    private sealed class FakeStateStillThere : IClusterStateProvider
    {
        public Task<ClusterState?> TryGetAsync(CancellationToken ct = default) =>
            Task.FromResult<ClusterState?>(new ClusterState(new[]
            {
                new LiveCt(9201, "testnode", "gone", "running", 1024L * 1024 * 1024, Cores: 2, Tags: "retired"),
                new LiveCt(9202, "testnode", "normal", "running", 1024L * 1024 * 1024, Cores: 8, Tags: ""),
            }));
    }

    private ConvergeRunner Runner(FakeExec exec, params string[] only) =>
        new(_dir, SecretsEnv.Load(null), exec: exec, only: only.Length == 0 ? null : only);

    private static async Task<(int rc, string output)> CaptureAsync(Func<Task<int>> run)
    {
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { return (await run(), sw.ToString()); }
        finally { Console.SetOut(prev); }
    }

    [Theory]
    [InlineData("retired", true)]
    [InlineData("RETIRED", true)]      // shapes are hand-written; don't be brittle about case
    [InlineData("describe-only", false)]
    [InlineData("managed", false)]
    [InlineData(null, false)]          // absent → managed, so existing shapes are unaffected
    [InlineData("", false)]
    public void IsRetired_ReadsTheMarker(string? manage, bool expected)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "x" } };
        s.Spec.Manage = manage;
        Assert.Equal(expected, s.IsRetired());

        var v = new VmShape { Metadata = new ShapeMetadata { Name = "x" } };
        v.Spec.Manage = manage;
        Assert.Equal(expected, v.IsRetired());
    }

    [Theory]
    [InlineData("retired")]
    [InlineData("describe-only")]
    public void IsReadOnly_CoversBothStances(string manage)
    {
        // Anything gating a WRITE asks this, so a future stance cannot be added without the
        // write paths honouring it.
        var s = new Shape { Metadata = new ShapeMetadata { Name = "x" } };
        s.Spec.Manage = manage;
        Assert.True(s.IsReadOnly());
    }

    // ---- the actual regression -------------------------------------------

    [Fact]
    public async Task Plan_DoesNotCountAnAbsentRetiredMemberAsSomethingToCreate()
    {
        // THE bug: "1 to create" is what an operator reads as pending work, and acting on it
        // is how CT 5113 came back.
        var exec = new FakeExec();
        var runner = new ConvergeRunner(_dir, SecretsEnv.Load(null), new FakeState(), exec: exec);
        var (rc, output) = await CaptureAsync(() => runner.PlanAsync());

        Assert.Equal(0, rc);
        Assert.Contains("RETIRED (superseded", output);
        Assert.Contains("1 retired", output);
        Assert.Contains("0 to create", output);

        var goneBlock = output[output.IndexOf("▸ gone", StringComparison.Ordinal)..];
        goneBlock = goneBlock[..goneBlock.IndexOf("▸ normal", StringComparison.Ordinal)];
        Assert.DoesNotContain("CREATE", goneBlock);
    }

    [Fact]
    public async Task Plan_FlagsARetiredMemberThatIsSomehowStillRunning()
    {
        // The post-#362 cleanup state: the shape says retired, the guest exists anyway. Silence
        // here would leave CT 5113 running forever with nothing pointing at it.
        var exec = new FakeExec();
        var runner = new ConvergeRunner(_dir, SecretsEnv.Load(null), new FakeStateStillThere(), exec: exec);
        var (_, output) = await CaptureAsync(() => runner.PlanAsync());

        Assert.Contains("RETIRED — but the guest still EXISTS", output);
    }

    [Fact]
    public async Task Apply_NeverCreatesARetiredMember()
    {
        var exec = new FakeExec();
        var (rc, output) = await CaptureAsync(() => Runner(exec).ApplyAsync());

        Assert.Equal(0, rc);
        Assert.Contains("SKIPPED: retired", output);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("9201", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_StillRefusesWhenTheRetiredMemberIsNamedInOnly()
    {
        // --only must not be an escape hatch. Opt-in safety on a destructive default is the
        // shape of mistake #325 and #362 both came from.
        var exec = new FakeExec();
        var (_, output) = await CaptureAsync(() => Runner(exec, "gone").ApplyAsync());

        Assert.Contains("SKIPPED: retired", output);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("9201", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Destroy_IsAllowed_UnlikeDescribeOnly()
    {
        // The one place the two stances differ. Destroy is how a retired guest gets cleaned
        // up through IaC rather than by hand, so refusing it would leave no supported route.
        var exec = new FakeExec();
        var (_, output) = await CaptureAsync(() => Runner(exec, "gone").DestroyAsync(confirmed: true));

        Assert.DoesNotContain("REFUSED", output);
    }
}
