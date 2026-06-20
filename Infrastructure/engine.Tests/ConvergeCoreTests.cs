using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Converge-core unit tests (issue #45). Pure / mocked — no live cluster.
public sealed class ConvergeCoreTests
{
    private static Shape Lxc(string name, string? ctid = null, params string[] dependsOn)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = name } };
        s.Spec.Ctid = ctid;
        s.Spec.DependsOn = dependsOn.ToList();
        return s;
    }

    // ---- TopologicalSorter ------------------------------------------------

    [Fact]
    public void TopologicalSorter_OrdersDiamondDependency()
    {
        // d depends on b and c; both depend on a:   a → {b,c} → d
        var a = Lxc("a");
        var b = Lxc("b", dependsOn: "a");
        var c = Lxc("c", dependsOn: "a");
        var d = Lxc("d", "100", "b", "c");

        var ordered = TopologicalSorter.Order(new[] { d, c, b, a });
        var pos = ordered.Select((s, i) => (s.Metadata.Name, i))
                         .ToDictionary(x => x.Name, x => x.i);

        Assert.Equal(4, ordered.Count);
        Assert.True(pos["a"] < pos["b"]);
        Assert.True(pos["a"] < pos["c"]);
        Assert.True(pos["b"] < pos["d"]);
        Assert.True(pos["c"] < pos["d"]);
    }

    [Fact]
    public void TopologicalSorter_ThrowsOnCycle()
    {
        var x = Lxc("x", dependsOn: "y");
        var y = Lxc("y", dependsOn: "x");

        var ex = Assert.Throws<InvalidOperationException>(() => TopologicalSorter.Order(new[] { x, y }));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TopologicalSorter_ThrowsOnUnknownDependency()
    {
        var a = Lxc("a", dependsOn: "ghost");

        var ex = Assert.Throws<InvalidOperationException>(() => TopologicalSorter.Order(new[] { a }));
        Assert.Contains("ghost", ex.Message);
        Assert.Contains("not a member", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- SecretResolver ---------------------------------------------------

    [Fact]
    public void SecretResolver_ReportsReady_WhenEnvVarPresent()
    {
        const string key = "HL45_TEST_PRESENT";
        Environment.SetEnvironmentVariable(key, "a-value");
        try
        {
            var env = SecretsEnv.Load(null); // reads process env
            var spec = new LxcSpec
            {
                Secrets = { new Secret { Name = "tok", ValueFrom = new SecretSource { Env = key } } },
            };

            var resolved = new SecretResolver(env).Plan(spec);
            var r = Assert.Single(resolved);
            Assert.True(r.Ready);
            Assert.Equal(SecretKind.Env, r.Kind);
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public void SecretResolver_ReportsNotReady_WhenEnvVarMissing()
    {
        const string key = "HL45_TEST_ABSENT";
        Environment.SetEnvironmentVariable(key, null); // ensure unset
        var env = SecretsEnv.Load(null);
        var spec = new LxcSpec
        {
            Secrets = { new Secret { Name = "tok", ValueFrom = new SecretSource { Env = key } } },
        };

        var resolved = new SecretResolver(env).Plan(spec);
        var r = Assert.Single(resolved);
        Assert.False(r.Ready);
        Assert.Contains("MISSING", r.Description);
    }

    // ---- StateDiffer (state-diff plan) ------------------------------------

    [Fact]
    public void StateDiffer_ReportsCreate_WhenCtAbsent()
    {
        var shape = Lxc("forgejo", "5001");
        var state = new ClusterState(Array.Empty<LiveCt>());

        var diff = StateDiffer.Diff(shape, state);
        Assert.Equal(ShapeDiffStatus.Create, diff.Status);
    }

    [Fact]
    public void StateDiffer_ReportsUpToDate_WhenMemoryMatches()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Node = "pve1";
        shape.Spec.Memory = 2048; // MB
        var live = new LiveCt(5001, "pve1", "forgejo", "running", 2048L * 1024 * 1024);
        var state = new ClusterState(new[] { live });

        var diff = StateDiffer.Diff(shape, state);
        Assert.Equal(ShapeDiffStatus.UpToDate, diff.Status);
        Assert.Empty(diff.Fields);
    }

    [Fact]
    public void StateDiffer_ReportsDrift_OnMemoryAndNode()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Node = "pve2";        // desired pve2
        shape.Spec.Memory = 4096;        // desired 4096 MB
        var live = new LiveCt(5001, "pve1", "forgejo", "running", 2048L * 1024 * 1024); // live pve1 / 2048MB
        var state = new ClusterState(new[] { live });

        var diff = StateDiffer.Diff(shape, state);
        Assert.Equal(ShapeDiffStatus.Drift, diff.Status);
        Assert.Contains(diff.Fields, f => f.Field == "memory");
        Assert.Contains(diff.Fields, f => f.Field == "node");
    }

    [Fact]
    public void StateDiffer_ReportsUnknown_WhenCtidNotNumeric()
    {
        var shape = Lxc("forgejo", "auto");
        var state = new ClusterState(Array.Empty<LiveCt>());

        var diff = StateDiffer.Diff(shape, state);
        Assert.Equal(ShapeDiffStatus.Unknown, diff.Status);
    }

    [Fact]
    public void StateDiffer_ReportsDrift_OnCores()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Cores = 4;                 // desired 4
        var live = new LiveCt(5001, "pve1", "forgejo", "running", null, Cores: 2);
        var state = new ClusterState(new[] { live });

        var diff = StateDiffer.Diff(shape, state);
        Assert.Equal(ShapeDiffStatus.Drift, diff.Status);
        var f = Assert.Single(diff.Fields, x => x.Field == "cores");
        Assert.Equal("4", f.Desired);
        Assert.Equal("2", f.Live);
    }

    [Fact]
    public void StateDiffer_TagsUpToDate_WhenSetMatchesRegardlessOfOrder()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Tags = new() { "iac", "media" };
        var live = new LiveCt(5001, "pve1", "forgejo", "running", null, Tags: "media;iac");
        var state = new ClusterState(new[] { live });

        var diff = StateDiffer.Diff(shape, state);
        Assert.Equal(ShapeDiffStatus.UpToDate, diff.Status);
    }

    [Fact]
    public void StateDiffer_ReportsDrift_OnTags()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Tags = new() { "iac", "media" };
        var live = new LiveCt(5001, "pve1", "forgejo", "running", null, Tags: "iac");
        var state = new ClusterState(new[] { live });

        var diff = StateDiffer.Diff(shape, state);
        Assert.Equal(ShapeDiffStatus.Drift, diff.Status);
        Assert.Contains(diff.Fields, f => f.Field == "tags");
    }

    [Fact]
    public void StateDiffer_IgnoresTags_WhenShapeDeclaresNone()
    {
        // Shape claims no tag ownership → live tags must not register as drift.
        var shape = Lxc("forgejo", "5001");
        var live = new LiveCt(5001, "pve1", "forgejo", "running", null, Tags: "manual;adhoc");
        var state = new ClusterState(new[] { live });

        var diff = StateDiffer.Diff(shape, state);
        Assert.Equal(ShapeDiffStatus.UpToDate, diff.Status);
    }

    // ---- CtConfigReconciler (update lifecycle) ----------------------------

    [Fact]
    public async Task CtConfigReconciler_NoChange_WhenCoresMemoryTagsMatch()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Node = "pve1";
        shape.Spec.Cores = 2;
        shape.Spec.Memory = 2048;
        shape.Spec.Tags = new() { "iac" };

        // Only `pct config` is allowed to run — any `pct set` means a false change.
        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("pct config")
                ? new ExecResult(0, "cores: 2\nmemory: 2048\ntags: iac\nhostname: forgejo", "")
                : throw new InvalidOperationException($"unexpected mutating command: {cmd}"));

        var result = await new CtConfigReconciler(exec).ReconcileAsync(shape);

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        Assert.Single(exec.Commands); // only the read-back
    }

    [Fact]
    public async Task CtConfigReconciler_SetsOnlyDifferingFields()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Node = "pve1";
        shape.Spec.Cores = 4;       // differs (live 2)
        shape.Spec.Memory = 2048;   // matches live → must NOT be set

        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("pct config")
                ? new ExecResult(0, "cores: 2\nmemory: 2048", "")
                : new ExecResult(0, "", "")); // pct set succeeds

        var result = await new CtConfigReconciler(exec).ReconcileAsync(shape);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var set = Assert.Single(exec.Commands, c => c.Contains("pct set"));
        Assert.Contains("--cores 4", set);
        Assert.DoesNotContain("--memory", set);
    }

    // ---- Destroy lifecycle ------------------------------------------------

    [Fact]
    public async Task CommunityScriptsCreator_Destroy_NoChange_WhenAbsent()
    {
        // `pct status` non-zero → CT absent → no stop/destroy issued.
        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("pct status")
                ? new ExecResult(2, "", "Configuration file does not exist")
                : throw new InvalidOperationException($"unexpected command on absent CT: {cmd}"));

        var result = await new CommunityScriptsCreator(exec).DestroyAsync("pve1", "5001", default);

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        Assert.Single(exec.Commands);
    }

    [Fact]
    public async Task CommunityScriptsCreator_Destroy_StopsRunning_ThenDestroys()
    {
        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("pct status") ? new ExecResult(0, "status: running", "")
                                       : new ExecResult(0, "", ""));

        var result = await new CommunityScriptsCreator(exec).DestroyAsync("pve1", "5001", default);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains(exec.Commands, c => c.Contains("pct stop 5001"));
        Assert.Contains(exec.Commands, c => c.Contains("pct destroy 5001"));
    }

    [Fact]
    public async Task CommunityScriptsCreator_Destroy_SkipsStop_WhenStopped()
    {
        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("pct status") ? new ExecResult(0, "status: stopped", "")
                                       : new ExecResult(0, "", ""));

        var result = await new CommunityScriptsCreator(exec).DestroyAsync("pve1", "5001", default);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct stop"));
        Assert.Contains(exec.Commands, c => c.Contains("pct destroy 5001"));
    }

    // ---- Provisioner idempotency (faked INodeExec seam) -------------------

    // Records pct-exec commands and replies from a scripted map. Lets us assert a
    // provisioner is idempotent WITHOUT a live cluster (issue #45).
    private sealed class FakeNodeExec : INodeExec
    {
        private readonly Func<string, ExecResult> _reply;
        public List<string> Commands { get; } = new();
        public FakeNodeExec(Func<string, ExecResult> reply) => _reply = reply;

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

    [Fact]
    public async Task ForgejoProvisioner_ReportsNoChange_WhenRootUrlAlreadyMatches()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Node = "pve1";
        shape.Spec.Config["rootUrl"] = "https://git.example.com/";

        // Read-back returns the already-desired ROOT_URL → must be a no-op.
        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("grep") && cmd.Contains("ROOT_URL")
                ? new ExecResult(0, "ROOT_URL = https://git.example.com/", "")
                : throw new InvalidOperationException($"unexpected mutating command: {cmd}"));

        var ctx = new ConvergeContext(exec, SecretsEnv.Load(null),
            new Dictionary<string, Shape>(), Deriver: null!);

        var result = await new ForgejoProvisioner().ApplyAsync(shape, ctx);

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        // Idempotency proof: only the read-back ran; no sed/systemctl restart.
        Assert.Single(exec.Commands);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("systemctl restart"));
    }

    [Fact]
    public async Task ForgejoProvisioner_AppliesChange_WhenRootUrlDiffers()
    {
        var shape = Lxc("forgejo", "5001");
        shape.Spec.Node = "pve1";
        shape.Spec.Config["rootUrl"] = "https://git.example.com/";

        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("grep") && cmd.Contains("ROOT_URL")
                ? new ExecResult(0, "ROOT_URL = https://OLD.example.com/", "")
                : new ExecResult(0, "", "")); // the sed+restart command succeeds

        var ctx = new ConvergeContext(exec, SecretsEnv.Load(null),
            new Dictionary<string, Shape>(), Deriver: null!);

        var result = await new ForgejoProvisioner().ApplyAsync(shape, ctx);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains(exec.Commands, c => c.Contains("systemctl restart forgejo"));
    }

    // ---- PangolinProvisioner ---------------------------------------------

    private static Shape PangolinShape()
    {
        var s = Lxc("pangolin", "2012");
        s.Spec.Node = "nuc-01";
        s.Spec.Config["dashboardUrl"] = "https://pangolin.chrison.dev";
        s.Spec.Config["baseDomain"] = "chrison.dev";
        s.Spec.Config["edge"] = "cloudflared";
        return s;
    }

    [Fact]
    public async Task PangolinProvisioner_ReportsNoChange_WhenMarkerMatches()
    {
        var shape = PangolinShape();
        var marker = PangolinProvisioner.DesiredMarker(shape);

        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("homelab-managed")
                ? new ExecResult(0, $"# homelab-managed: {marker}", "")
                : throw new InvalidOperationException($"unexpected command: {cmd}"));
        var ctx = new ConvergeContext(exec, SecretsEnv.Load(null),
            new Dictionary<string, Shape>(), Deriver: null!);

        var result = await new PangolinProvisioner().ApplyAsync(shape, ctx);

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        Assert.Single(exec.Commands); // only the marker read — no IP probe, no write
    }

    [Fact]
    public async Task PangolinProvisioner_SeedsConfigAndReshapesTraefik_WhenMarkerStale()
    {
        var shape = PangolinShape();

        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("homelab-managed") ? new ExecResult(0, "# homelab-managed: stale00000000", "")
            : cmd.Contains("hostname -I") ? new ExecResult(0, "10.10.5.5", "")
            : new ExecResult(0, "", "")); // the write + restart command
        var ctx = new ConvergeContext(exec, SecretsEnv.Load(null),
            new Dictionary<string, Shape>(), Deriver: null!);

        var result = await new PangolinProvisioner().ApplyAsync(shape, ctx);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var write = Assert.Single(exec.Commands, c => c.Contains("config.yml <<EOF"));
        Assert.Contains("dashboard_url: \"https://pangolin.chrison.dev\"", write);
        Assert.Contains("allow_raw_resources: true", write);
        Assert.Contains("secret: \"$SECRET\"", write); // server.secret generated/preserved on the CT
        Assert.Contains("openssl rand", write);         // generated when absent
        // behind-cloudflared Traefik: routers on :80, no Let's Encrypt, no https redirect
        Assert.Contains("dynamic_config.yml <<DYN", write);
        Assert.Contains("- web", write);
        Assert.DoesNotContain("websecure", write);
        Assert.DoesNotContain("certResolver", write);
        Assert.DoesNotContain("redirect", write);
        Assert.Contains("systemctl restart pangolin gerbil", write);
    }

    [Fact]
    public void PangolinProvisioner_Marker_ChangesWithEdgeMode()
    {
        var a = PangolinShape();
        var b = PangolinShape();
        b.Spec.Config["edge"] = "letsencrypt";
        Assert.NotEqual(PangolinProvisioner.DesiredMarker(a), PangolinProvisioner.DesiredMarker(b));
    }
}
