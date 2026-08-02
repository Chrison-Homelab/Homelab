using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// `spec.manage: describe-only` (issue #325). An adopted guest's live config can never
// fully match the shape describing it, so converge reported it as drift on every run and
// every apply wanted to write to it. #306's `--only` made that avoidable; this makes it
// impossible — the constraint lives in the shape, not in the operator's memory.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class DescribeOnlyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hl325-" + Guid.NewGuid().ToString("n")[..12]);

    public DescribeOnlyTests()
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
        File.WriteAllText(Path.Combine(_dir, "adopted.lxc.yaml"), """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: adopted
            spec:
              app: mqtt
              ctid: 9101
              cores: 2
              manage: describe-only
            """);
        File.WriteAllText(Path.Combine(_dir, "normal.lxc.yaml"), """
            apiVersion: homelab/v1
            kind: LXC
            metadata:
              name: normal
            spec:
              app: mqtt
              ctid: 9102
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

    private ConvergeRunner Runner(FakeExec exec, params string[] only) =>
        new(_dir, SecretsEnv.Load(null), exec: exec, only: only.Length == 0 ? null : only);

    // ---- the marker itself ------------------------------------------------

    [Theory]
    [InlineData("describe-only", true)]
    [InlineData("DESCRIBE-ONLY", true)]   // shapes are hand-written; don't be brittle about case
    [InlineData("managed", false)]
    [InlineData(null, false)]             // absent → managed, so existing shapes are unaffected
    [InlineData("", false)]
    public void IsDescribeOnly_ReadsTheMarker(string? manage, bool expected)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "x" } };
        s.Spec.Manage = manage;
        Assert.Equal(expected, s.IsDescribeOnly());

        var v = new VmShape { Metadata = new ShapeMetadata { Name = "x" } };
        v.Spec.Manage = manage;
        Assert.Equal(expected, v.IsDescribeOnly());
    }

    // ---- apply ------------------------------------------------------------

    [Fact]
    public async Task Apply_SkipsTheDescribeOnlyMember_AndTouchesTheOtherOne()
    {
        var exec = new FakeExec();
        var rc = await Runner(exec).ApplyAsync();

        Assert.Equal(0, rc);
        // The adopted CT is never even probed for existence, let alone written.
        Assert.DoesNotContain(exec.Commands, c => c.Contains("9101", StringComparison.Ordinal));
        // …while the managed one is handled normally, proving the skip is targeted.
        Assert.Contains(exec.Commands, c => c == "pct status 9102");
    }

    [Fact]
    public async Task Apply_NamingItInOnly_StillDoesNotWriteToIt()
    {
        // The regression that would defeat the whole point: if --only could override the
        // marker, the guarantee is back in the operator's hands.
        var exec = new FakeExec();
        var rc = await Runner(exec, "adopted").ApplyAsync();

        Assert.Equal(0, rc);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("9101", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_CountsItAsSkipped_NotApplied()
    {
        var exec = new FakeExec();
        var (rc, output) = await CaptureAsync(() => Runner(exec, "adopted").ApplyAsync());

        Assert.Equal(0, rc);
        Assert.Contains("SKIPPED: describe-only", output);
        Assert.Contains("1 skipped", output);
        Assert.Contains("0 applied", output);
    }

    // ---- plan -------------------------------------------------------------

    // Live state that WOULD drift both members: both fixtures declare `cores: 2` and this
    // reports 8. That declaration matters — StateDiffer only compares a field the shape
    // actually claims, so without it both members read as up-to-date and asserting "no
    // drift reported" would prove nothing.
    private sealed class FakeState : IClusterStateProvider
    {
        public Task<ClusterState?> TryGetAsync(CancellationToken ct = default) =>
            Task.FromResult<ClusterState?>(new ClusterState(new[]
            {
                new LiveCt(9101, "testnode", "adopted", "running", 1024L * 1024 * 1024, Cores: 8, Tags: "hand;made"),
                new LiveCt(9102, "testnode", "normal", "running", 1024L * 1024 * 1024, Cores: 8, Tags: "hand;made"),
            }));
    }

    [Fact]
    public async Task Plan_ReportsDescribeOnly_AndKeepsItOutOfTheDriftCount()
    {
        var exec = new FakeExec();
        var runner = new ConvergeRunner(_dir, SecretsEnv.Load(null), new FakeState(), exec: exec);
        var (rc, output) = await CaptureAsync(() => runner.PlanAsync());

        Assert.Equal(0, rc);
        Assert.Contains("DESCRIBE-ONLY (adopted", output);
        Assert.Contains("1 describe-only", output);

        // The adopted member's own drift lines must be absent. Scope the check to its
        // block so the managed member's (legitimate) drift output can't mask a regression.
        var adoptedBlock = output[output.IndexOf("▸ adopted", StringComparison.Ordinal)..];
        adoptedBlock = adoptedBlock[..adoptedBlock.IndexOf("▸ normal", StringComparison.Ordinal)];
        Assert.DoesNotContain("DRIFT", adoptedBlock);
    }

    [Fact]
    public async Task Plan_StillReportsDriftForManagedMembers()
    {
        // Guard against over-reach: the marker must suppress ONE member, not disable the
        // drift machinery. Both fixtures diverge from FakeState identically.
        var exec = new FakeExec();
        var runner = new ConvergeRunner(_dir, SecretsEnv.Load(null), new FakeState(), exec: exec);
        var (_, output) = await CaptureAsync(() => runner.PlanAsync());

        var normalBlock = output[output.IndexOf("▸ normal", StringComparison.Ordinal)..];
        Assert.Contains("DRIFT", normalBlock);
        Assert.Contains("1 drifted", output);
    }

    // ---- destroy ----------------------------------------------------------

    [Fact]
    public async Task Destroy_RefusesIt_EvenWhenConfirmed()
    {
        // Adopted generally means older and less replaceable than the stack describing it,
        // so this is the verb where getting it wrong is unrecoverable.
        var exec = new FakeExec();
        var (_, output) = await CaptureAsync(() => Runner(exec, "adopted").DestroyAsync(confirmed: true));

        Assert.Contains("REFUSED", output);
        Assert.Contains("1 refused", output);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct destroy", StringComparison.Ordinal));
        Assert.DoesNotContain(exec.Commands, c => c.Contains("9101", StringComparison.Ordinal));
    }

    // ---- harness ----------------------------------------------------------

    // The runner reports through Console, so assertions about counts/labels have to read
    // stdout. Restored in a finally so a failure here can't swallow the rest of the run.
    private static async Task<(int Rc, string Output)> CaptureAsync(Func<Task<int>> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try { var rc = await run(); return (rc, buffer.ToString()); }
        finally { Console.SetOut(original); }
    }
}
