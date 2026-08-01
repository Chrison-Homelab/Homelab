using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// PlexProvisioner (#332) — reconciles Plex server settings through the /:/prefs API.
//
// The setting that motivated this is TranscoderCanOnlyRemuxVideo=1 ("Disable video stream
// transcoding"), a deliberate NON-default that lived only inside the container. A rebuild
// would have reset it to Plex's default and let a 4-core box start accepting 4K video
// transcodes it cannot serve.
public sealed class PlexProvisionerTests
{
    private static Shape Plex(params (string Key, object Value)[] prefs)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "plex" } };
        s.Spec.App = "plex";
        s.Spec.Node = "hpe-01";
        s.Spec.Ctid = "5008";
        if (prefs.Length > 0)
        {
            var d = new Dictionary<string, object?>();
            foreach (var (k, v) in prefs) d[k] = v;
            s.Spec.Config["prefs"] = d;
        }
        return s;
    }

    private sealed class FakeExec : INodeExec
    {
        private readonly Func<string, ExecResult> _reply;
        public List<string> Commands { get; } = new();
        public FakeExec(Func<string, ExecResult> reply) => _reply = reply;
        public Task<ExecResult> OnNodeAsync(string n, string c, CancellationToken ct = default)
        { Commands.Add(c); return Task.FromResult(_reply(c)); }
        public Task<ExecResult> InContainerAsync(string n, string id, string c, CancellationToken ct = default)
        { Commands.Add(c); return Task.FromResult(_reply(c)); }
    }

    private static ConvergeContext Ctx(INodeExec exec) =>
        new(exec, SecretsEnv.Load(null), new Dictionary<string, Shape>(), Deriver: null!);

    // ---- registration -----------------------------------------------------

    [Fact]
    public void Registry_DispatchesPlexApp_ToPlexProvisioner()
    {
        var s = Plex();
        Assert.IsType<PlexProvisioner>(ProvisionerRegistry.Default().For(s.Spec.App));
    }

    // ---- value normalisation ----------------------------------------------

    [Fact]
    public void BooleanPrefs_NormaliseTo1And0()
    {
        // Plex's API takes bools as 1/0 and REPORTS them as 1/0. A YAML `true` left as
        // "True" would both be rejected and compare unequal to what Plex returns, so every
        // run would look like drift and re-PUT forever.
        var prefs = PlexProvisioner.DeclaredPrefs(
            Plex(("TranscoderCanOnlyRemuxVideo", true), ("HardwareAcceleratedCodecs", false)));
        Assert.Equal("1", prefs["TranscoderCanOnlyRemuxVideo"]);
        Assert.Equal("0", prefs["HardwareAcceleratedCodecs"]);
    }

    [Fact]
    public void NonBooleanPrefs_PassThroughAsStrings()
    {
        var prefs = PlexProvisioner.DeclaredPrefs(
            Plex(("TranscoderQuality", 2), ("TranscoderH264BackgroundPreset", "veryfast")));
        Assert.Equal("2", prefs["TranscoderQuality"]);
        Assert.Equal("veryfast", prefs["TranscoderH264BackgroundPreset"]);
    }

    [Fact]
    public void NoPrefsDeclared_YieldsEmptyMap()
    {
        Assert.Empty(PlexProvisioner.DeclaredPrefs(Plex()));
    }

    // ---- apply ------------------------------------------------------------

    [Fact]
    public async Task NoPrefsDeclared_TouchesNothing()
    {
        // Add/update only: a shape that declares no prefs must leave Plex entirely alone,
        // not "reconcile" it to some implied baseline.
        var exec = new FakeExec(_ => new ExecResult(0, "", ""));
        var r = await new PlexProvisioner().ApplyAsync(Plex(), Ctx(exec));

        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
        Assert.Empty(exec.Commands);
    }

    [Fact]
    public async Task InSync_ReportsNoChange_AndDoesNotWrite()
    {
        var exec = new FakeExec(c => new ExecResult(0, "NOCHANGE", ""));
        var r = await new PlexProvisioner().ApplyAsync(Plex(("TranscoderCanOnlyRemuxVideo", true)), Ctx(exec));

        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
        Assert.Single(exec.Commands);                                    // the check only
        Assert.Contains("check", exec.Commands[0]);
        Assert.DoesNotContain(exec.Commands, c => c.Contains(" write", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drift_RunsWriteAndReportsWhatChanged()
    {
        var exec = new FakeExec(c => c.Contains(" write", StringComparison.Ordinal)
            ? new ExecResult(0, "WROTE TranscoderCanOnlyRemuxVideo: '0'->'1'", "")
            : new ExecResult(0, "CHANGED TranscoderCanOnlyRemuxVideo: '0'->'1'", ""));
        var r = await new PlexProvisioner().ApplyAsync(Plex(("TranscoderCanOnlyRemuxVideo", true)), Ctx(exec));

        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.Contains("TranscoderCanOnlyRemuxVideo", r.Message);
        Assert.Contains("'0'->'1'", r.Message);                          // before → after is surfaced
        Assert.Equal(2, exec.Commands.Count);                            // check, then write
    }

    [Fact]
    public async Task MotdBanner_DoesNotLeakIntoTheResultMessage()
    {
        // `pct exec` into a community-scripts CT prints an ANSI MOTD banner before the
        // command's own output. Taking stdout wholesale reported "Plex LXC Container" as the
        // change description on the first live run — caught only by reading the output.
        const string banner = "[1mPlex LXC Container[m\n   OS: Debian\n";
        var exec = new FakeExec(c => c.Contains(" write", StringComparison.Ordinal)
            ? new ExecResult(0, banner + "WROTE HardwareAcceleratedCodecs: '0'->'1'", "")
            : new ExecResult(0, banner + "CHANGED HardwareAcceleratedCodecs: '0'->'1'", ""));
        var r = await new PlexProvisioner().ApplyAsync(Plex(("HardwareAcceleratedCodecs", true)), Ctx(exec));

        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.DoesNotContain("LXC Container", r.Message);
        Assert.DoesNotContain("Debian", r.Message);
        Assert.Contains("HardwareAcceleratedCodecs: '0'->'1'", r.Message);
    }

    // ---- failure modes ---------------------------------------------------

    [Theory]
    [InlineData("UNREACHABLE", "not answering")]
    [InlineData("NOTOKEN", "not signed in")]
    public async Task DiagnosableFailures_FailWithAReadableReason(string stdout, string expect)
    {
        // These must FAIL rather than read as "nothing to do" — reporting success when the
        // server could not be queried is how silent config drift happens.
        var exec = new FakeExec(_ => new ExecResult(0, stdout, ""));
        var r = await new PlexProvisioner().ApplyAsync(Plex(("HardwareAcceleratedCodecs", true)), Ctx(exec));

        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.Contains(expect, r.Message);
    }

    [Fact]
    public async Task UnknownPrefKey_Fails_RatherThanLoopingForever()
    {
        // A declared key Plex doesn't recognise is a typo in the shape. Treating it as drift
        // would re-PUT it on every single converge and never converge.
        var exec = new FakeExec(_ => new ExecResult(1, "UNKNOWN TranscoderCanOnlyRemuxVidoe", ""));
        var r = await new PlexProvisioner().ApplyAsync(Plex(("TranscoderCanOnlyRemuxVidoe", true)), Ctx(exec));

        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.Contains("UNKNOWN", r.Message);
    }

    [Fact]
    public async Task MissingNodeOrCtid_Fails()
    {
        var s = Plex(("HardwareAcceleratedCodecs", true));
        s.Spec.Ctid = null;
        var r = await new PlexProvisioner().ApplyAsync(s, Ctx(new FakeExec(_ => new ExecResult(0, "", ""))));
        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
    }

    // ---- plan -------------------------------------------------------------

    [Fact]
    public void PlanSteps_NameTheDeclaredKeysAndSayAddUpdateOnly()
    {
        var steps = new PlexProvisioner().PlanSteps(Plex(("TranscoderCanOnlyRemuxVideo", true))).ToList();
        var text = string.Join("\n", steps);
        Assert.Contains("TranscoderCanOnlyRemuxVideo=1", text);
        Assert.Contains("add/update only", text);
    }

    [Fact]
    public void PlanSteps_SayNothingIsTouchedWhenNoPrefsDeclared()
    {
        Assert.Contains("untouched", string.Join("\n", new PlexProvisioner().PlanSteps(Plex())));
    }
}
