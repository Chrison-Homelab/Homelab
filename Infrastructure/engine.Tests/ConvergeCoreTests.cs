using System.Text.Json;
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

    [Fact]
    public async Task CtConfigReconciler_QuotesTagsSeparator()
    {
        // Proxmox joins tags with ';'. The `pct set` arg must be quoted, or the remote
        // shell reads ';' as a command separator (#114 live smoke test caught this:
        // `pct set … --tags a;b` ran `b` as a command → "command not found").
        var shape = Lxc("smoketest", "9099");
        shape.Spec.Node = "nuc-01";
        shape.Metadata.Tags = new List<string> { "smoketest", "throwaway" };

        var exec = new FakeNodeExec(cmd =>
            cmd.Contains("pct config")
                ? new ExecResult(0, "cores: 1\nmemory: 512", "") // no tags live → differs
                : new ExecResult(0, "", ""));

        var result = await new CtConfigReconciler(exec).ReconcileAsync(shape);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var set = Assert.Single(exec.Commands, c => c.Contains("pct set"));
        Assert.Matches(@"--tags ""[^""]*;[^""]*""", set); // ';' lives INSIDE the quotes
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

    // ---- NodeExec node→address resolution (issue #162) --------------------

    [Fact]
    public void NodeExecResolve_FallsBackToName_WhenNoOverride()
    {
        // unique name → no env override exists → resolves to the name unchanged.
        Assert.Equal("node-xyzzy-01", NodeExec.Resolve("node-xyzzy-01"));
    }

    [Fact]
    public void NodeExecResolve_UsesEnvOverride_MappingNameToIp()
    {
        // NODE_ADDR_<NAME>: name uppercased, non-alphanumerics → underscore.
        Environment.SetEnvironmentVariable("NODE_ADDR_HPE_TEST_01", "10.0.0.9");
        try { Assert.Equal("10.0.0.9", NodeExec.Resolve("hpe-test-01")); }
        finally { Environment.SetEnvironmentVariable("NODE_ADDR_HPE_TEST_01", null); }
    }

    // ---- arr-wire parse helpers (#159) ------------------------------------

    [Fact]
    public void ArrExec_ParseApiKey_ExtractsFromConfigXml()
    {
        var xml = "<Config>\n  <Port>8989</Port>\n  <ApiKey>abc123def4567890abcdef0123456789</ApiKey>\n</Config>";
        Assert.Equal("abc123def4567890abcdef0123456789", ArrExec.ParseApiKey(xml));
        Assert.Null(ArrExec.ParseApiKey("<Config><Port>8989</Port></Config>"));
    }

    [Fact]
    public void ArrExec_ParseQbitTempPassword_TakesLatestSessionPassword()
    {
        var journal = string.Join('\n',
            "... A temporary password is provided for this session: OLDpw11",
            "some other line",
            "... A temporary password is provided for this session: NEWpw22");
        Assert.Equal("NEWpw22", ArrExec.ParseQbitTempPassword(journal));
        Assert.Null(ArrExec.ParseQbitTempPassword("nothing here"));
    }

    // ---- qBittorrent download-client dialects (#363) ----------------------
    //
    // The two ways a Servarr download-client resource differs per app. Both were
    // wrong once: Prowlarr NRE'd on the missing `categories` list, and Sonarr/Radarr
    // silently dropped a category posted under the wrong field name.

    private static JsonElement QbitBody(string category, ArrExec.QbitClientDialect dialect) =>
        JsonDocument.Parse(ArrExec.QbitDownloadClientJson(
            "10.0.0.1", "user", "pw", category, dialect)).RootElement;

    private static string? FieldValue(JsonElement body, string name) =>
        body.GetProperty("fields").EnumerateArray()
            .Where(f => f.GetProperty("name").GetString() == name)
            .Select(f => f.GetProperty("value").ToString())
            .FirstOrDefault();

    [Theory]
    [InlineData("tvCategory", "tv-sonarr")]
    [InlineData("movieCategory", "radarr")]
    [InlineData("category", "prowlarr")]
    public void QbitDownloadClientJson_NamesTheCategoryFieldPerApp(string field, string category)
    {
        var dialect = field switch
        {
            "tvCategory" => ArrExec.QbitClientDialect.Sonarr,
            "movieCategory" => ArrExec.QbitClientDialect.Radarr,
            _ => ArrExec.QbitClientDialect.Prowlarr,
        };
        var body = QbitBody(category, dialect);

        Assert.Equal(category, FieldValue(body, field));
        // Exactly one category field — a stray `category` on Sonarr is dropped silently
        // by the server, so it must not be posted at all.
        Assert.Single(body.GetProperty("fields").EnumerateArray(),
            f => f.GetProperty("name").GetString()!.Contains("ategory"));
    }

    [Fact]
    public void QbitDownloadClientJson_SendsCategoriesListForProwlarrOnly()
    {
        // Prowlarr: present and empty ("all categories"). Absent => null server-side =>
        // NullReferenceException in DownloadClientBase.ValidateCategories during the
        // pre-save connection test, which is what failed every Media deploy.
        var prowlarr = QbitBody("prowlarr", ArrExec.QbitClientDialect.Prowlarr);
        Assert.True(prowlarr.TryGetProperty("categories", out var cats));
        Assert.Equal(JsonValueKind.Array, cats.ValueKind);
        Assert.Empty(cats.EnumerateArray());

        // Sonarr/Radarr have no such property on their definition — don't invent one.
        Assert.False(QbitBody("tv-sonarr", ArrExec.QbitClientDialect.Sonarr)
            .TryGetProperty("categories", out _));
        Assert.False(QbitBody("radarr", ArrExec.QbitClientDialect.Radarr)
            .TryGetProperty("categories", out _));
    }

    [Fact]
    public void QbitDownloadClientJson_KeepsTheSharedConnectionFields()
    {
        var body = QbitBody("prowlarr", ArrExec.QbitClientDialect.Prowlarr);

        Assert.Equal("qBittorrent", body.GetProperty("name").GetString());
        Assert.Equal("QBittorrent", body.GetProperty("implementation").GetString());
        Assert.Equal("QBittorrentSettings", body.GetProperty("configContract").GetString());
        Assert.True(body.GetProperty("enable").GetBoolean());
        Assert.Equal("10.0.0.1", FieldValue(body, "host"));
        Assert.Equal(ArrExec.QbitWebUiPort.ToString(), FieldValue(body, "port"));
        Assert.Equal("user", FieldValue(body, "username"));
        Assert.Equal("pw", FieldValue(body, "password"));
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

    // ---- Provisioner override dispatch (#168: docker host + pangolin provisioner) --

    [Fact]
    public void Registry_DispatchesByProvisionerOverride_ElseApp()
    {
        var reg = ProvisionerRegistry.Default();
        // app: docker + provisioner: pangolin → PangolinProvisioner (the create path still
        // uses ct/docker.sh via app, but post-create is dispatched by the override).
        var s = Lxc("pangolin", "2013");
        s.Spec.App = "docker";
        s.Spec.Provisioner = "pangolin";
        Assert.Equal("pangolin", reg.For(s.Spec.Provisioner ?? s.Spec.App).App);

        // No override → dispatch by app (docker has no provisioner → DefaultProvisioner "*").
        var y = Lxc("youtarr", "5113");
        y.Spec.App = "docker";
        Assert.Equal("*", reg.For(y.Spec.Provisioner ?? y.Spec.App).App);
    }

    // ---- PangolinProvisioner Docker-EE public-wildcard (#168 / ADR-0007) --

    private static Shape PangolinWildcardShape()
    {
        var s = Lxc("pangolin", "2013");
        s.Spec.Node = "nuc-01";
        s.Spec.Config["dashboardUrl"] = "https://pangolin.chrison.dev";
        s.Spec.Config["baseDomain"] = "chrison.dev";
        s.Spec.Config["edge"] = "public-wildcard";
        s.Spec.Config["letsEncryptEmail"] = "csimon@chrison.dev";
        s.Spec.Config["resources"] = new List<object>
        {
            new Dictionary<object, object> { ["name"] = "Radarr", ["subdomain"] = "radarr", ["zone"] = "arr" },
            new Dictionary<object, object> { ["name"] = "Grafana", ["subdomain"] = "grafana", ["zone"] = "lab" },
        };
        return s;
    }

    [Fact]
    public void Pangolin_Marker_DiffersByEdgeAndImage()
    {
        var cf = PangolinShape();                       // edge: cloudflared
        var wc = PangolinWildcardShape();               // edge: public-wildcard
        Assert.NotEqual(PangolinProvisioner.DesiredMarker(cf), PangolinProvisioner.DesiredMarker(wc));

        var wc2 = PangolinWildcardShape();
        wc2.Spec.Config["image"] = "fosrl/pangolin:ee-1.20.0";
        Assert.NotEqual(PangolinProvisioner.DesiredMarker(wc), PangolinProvisioner.DesiredMarker(wc2));
    }

    [Fact]
    public void Pangolin_WildcardZones_DeriveFromResources()
    {
        var s = PangolinWildcardShape();
        Assert.Equal(new[] { "arr", "lab" }, PangolinProvisioner.WildcardZones(s).OrderBy(z => z));
        Assert.Contains("*.arr.chrison.dev", PangolinProvisioner.WildcardFqdns(s));
        Assert.Contains("*.lab.chrison.dev", PangolinProvisioner.WildcardFqdns(s));
    }

    // ── Multiple base domains (#322) ───────────────────────────────────────────────
    // Pangolin has no API to create a domain, so config.yml is the only way in — which is
    // why fronting a second registrable domain needed engine support rather than a config
    // edit. baseDomain must stay domain1: Pangolin keys resources by domainId.

    [Fact]
    public void Pangolin_AdditionalDomains_AreListedAfterBaseDomain()
    {
        var s = PangolinWildcardShape();
        s.Spec.Config["additionalDomains"] = new List<object> { "tao-simon.family", "example.test" };
        Assert.Equal(new[] { "chrison.dev", "tao-simon.family", "example.test" }, PangolinProvisioner.AllDomains(s));
        Assert.Equal(new[] { "tao-simon.family", "example.test" }, PangolinProvisioner.AdditionalDomains(s));
    }

    [Fact]
    public void Pangolin_AdditionalDomains_IgnoreDuplicatesAndTheBaseDomain()
    {
        var s = PangolinWildcardShape();
        s.Spec.Config["additionalDomains"] = new List<object> { "chrison.dev", "tao-simon.family", "tao-simon.family" };
        Assert.Equal(new[] { "chrison.dev", "tao-simon.family" }, PangolinProvisioner.AllDomains(s));
    }

    [Fact]
    public void Pangolin_AdditionalDomain_GetsApexPlusOneWildcardLevel_NotZoned()
    {
        var s = PangolinWildcardShape();
        s.Spec.Config["additionalDomains"] = new List<object> { "tao-simon.family" };

        // The zoned base-domain SANs are untouched...
        Assert.Contains("*.arr.chrison.dev", PangolinProvisioner.WildcardFqdns(s));
        // ...and the extra domain is fronted at its own apex, one wildcard level up.
        Assert.Contains("*.tao-simon.family", PangolinProvisioner.WildcardFqdns(s));
        Assert.DoesNotContain("*.arr.tao-simon.family", PangolinProvisioner.WildcardFqdns(s));

        var st = PangolinProvisioner.BuildTraefikStatic(s, "chrison.dev");
        Assert.Contains("- main: \"tao-simon.family\"", st);
        Assert.Contains("sans: [\"*.tao-simon.family\"]", st);
        Assert.Contains("sans: [\"*.arr.chrison.dev\"]", st);   // base domain still covered
    }

    [Fact]
    public void Pangolin_Marker_ChangesWhenAnAdditionalDomainIsAdded()
    {
        var before = PangolinWildcardShape();
        var after = PangolinWildcardShape();
        after.Spec.Config["additionalDomains"] = new List<object> { "tao-simon.family" };
        // Without this the config.yml + Traefik SANs would change but converge would report
        // "config current" and never re-render.
        Assert.NotEqual(PangolinProvisioner.DesiredMarker(before), PangolinProvisioner.DesiredMarker(after));
    }

    [Fact]
    public void Pangolin_GerbilBaseEndpoint_IsThePublicIp_NotTheCloudflareProxiedDashboard()
    {
        // The dashboard is served through the core Cloudflare tunnel, so its hostname resolves
        // to Cloudflare — and Cloudflare's proxy does not carry WireGuard UDP. A Newt connector
        // handed that endpoint sends handshakes into a black hole. In public-wildcard mode the
        // endpoint must be the home WAN IP the 51820/udp forward lives behind.
        var s = PangolinWildcardShape();
        s.Spec.Config["publicIp"] = "203.0.113.7";

        Assert.Equal("203.0.113.7", PangolinProvisioner.GerbilBaseEndpoint(s, "pangolin.chrison.dev"));
    }

    [Fact]
    public void Pangolin_Marker_ChangesWhenTheGerbilEndpointChanges()
    {
        // #437 hashed the compose and Traefik config but not config.yml — where base_endpoint
        // lives, and whose inputs appear in no other marker component. So the first attempt at
        // the endpoint fix rendered a new config.yml and still reported NOCHANGE.
        var before = PangolinWildcardShape();
        before.Spec.Config["publicIp"] = "203.0.113.7";
        var after = PangolinWildcardShape();
        after.Spec.Config["publicIp"] = "203.0.113.7";
        after.Spec.Config["gerbilEndpoint"] = "edge.example.net";

        Assert.NotEqual(PangolinProvisioner.DesiredMarker(before), PangolinProvisioner.DesiredMarker(after));
    }

    [Fact]
    public void Pangolin_GerbilBaseEndpoint_IsOverridableForTheVpsGraduation()
    {
        // When Gerbil moves off-site, connectors dial the VPS — this is the knob that turns.
        var s = PangolinWildcardShape();
        s.Spec.Config["publicIp"] = "203.0.113.7";
        s.Spec.Config["gerbilEndpoint"] = "edge.example.net";

        Assert.Equal("edge.example.net", PangolinProvisioner.GerbilBaseEndpoint(s, "pangolin.chrison.dev"));
    }

    [Fact]
    public void Pangolin_GerbilBaseEndpoint_FallsBackToTheDashboardHost_WhenNoPublicIp()
    {
        // cloudflared mode has no public :443 and no WireGuard path — keep prior behaviour
        // rather than inventing an endpoint.
        var s = PangolinWildcardShape();
        s.Spec.Config.Remove("publicIp");

        Assert.Equal("pangolin.chrison.dev", PangolinProvisioner.GerbilBaseEndpoint(s, "pangolin.chrison.dev"));
    }

    [Fact]
    public void Pangolin_Marker_TracksTheDeployRecipe_NotOnlyItsOutputs()
    {
        // The marker must move when the RECIPE changes, not just its inputs. Without this a
        // corrected deploy script silently no-ops on every host carrying the old marker — which
        // is exactly how the missing `docker compose restart` could not be shipped: the fix was
        // right, the marker did not move, converge reported NOCHANGE.
        //
        // Asserted structurally: the marker must depend on the generated script, so a shape
        // whose only difference shows up IN the script still moves it.
        var s = PangolinWildcardShape();
        var script = PangolinProvisioner.BuildDockerDeploy(s, "<marker>",
            PangolinProvisioner.DashboardHost(s), PangolinProvisioner.DashboardUrl(s),
            "chrison.dev", "<cfToken>");

        // The restart is part of the recipe, so it is inside what the marker hashes.
        Assert.Contains("docker compose restart", script, StringComparison.Ordinal);

        var before = PangolinProvisioner.DesiredMarker(s);
        var after = PangolinWildcardShape();
        after.Spec.Config["gerbilEndpoint"] = "10.10.0.13";
        Assert.NotEqual(before, PangolinProvisioner.DesiredMarker(after));
    }

    [Fact]
    public void Pangolin_ShellResource_IsAnHttpsWebTerminalNotAnSshResource()
    {
        // Supersedes the #440 contract (mode: ssh on port 22). #479 replaced that front door with
        // Zellij's own web client, and the reason was not the UI: Pangolin's SSH resource asks for
        // the HOST's credentials after the SSO gate, so it forced an OS password onto the account.
        // A web resource needs none, so SHELL_USER_PASSWORD is retired and the box is key-only.
        //
        // This test is the guard against quietly regressing to the SSH resource and re-introducing
        // that password: no resource in the shape may carry mode: ssh.
        var stackDir = Path.GetDirectoryName(FindRepoFile(Path.Combine("stacks", "Core", "pangolin.lxc.yaml")))!;
        // Core's shapes carry `${HOME_WAN_IP}` (ShapeVars), and an unresolved variable is a
        // hard load failure by design — so give the loader the values rather than depending
        // on whoever runs the suite having a secrets.env.
        var shape = ShapeLoader.LoadStack(stackDir, StubHomeEdgeVars())
            .Members.Single(m => m.Metadata.Name == "pangolin");
        var resources = (System.Collections.IEnumerable)shape.Spec.Config["resources"]!;

        System.Collections.IDictionary? shell = null;
        foreach (var r in resources)
        {
            if (r is not System.Collections.IDictionary d) continue;
            Assert.NotEqual("ssh", d["mode"]?.ToString()?.ToLowerInvariant());
            if (string.Equals(d["subdomain"]?.ToString(), "shell", StringComparison.OrdinalIgnoreCase))
                shell = d;
        }

        Assert.NotNull(shell);
        // Still on the DevOps Newt site — that part of #441 survives the swap.
        Assert.Equal("DevOps", shell!["site"]?.ToString());
        // sso is NOT optional: zellij's web server has no rate limiting and its docs state it
        // assumes authenticated users are trusted, so the Pangolin/Authentik gate is what makes
        // exposing a shell defensible at all.
        Assert.Equal("True", shell["sso"]?.ToString(), ignoreCase: true);

        var target = (System.Collections.IDictionary)shell["target"]!;
        Assert.Equal("8082", target["port"]?.ToString());
        // https, and it must be: zellij refuses a non-loopback bind without a certificate and
        // offers no insecure override, so the target genuinely speaks TLS.
        Assert.Equal("https", target["method"]?.ToString());
        // An IP, not a name — the connector resolves via 9.9.9.9 and cannot see *.internal.
        Assert.DoesNotContain("internal", target["ip"]?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Pangolin_GerbilCompose_AdvertisesThePortItActuallyListensOn()
    {
        // gerbil's -listen defaults to ":3003". Advertising :3004 made every Pangolin→gerbil
        // peer call fail with ECONNREFUSED, so Newt never received a wg config and the tunnel
        // never formed — while the site still reported online, because the websocket control
        // plane was healthy. The two ports must agree.
        var s = PangolinWildcardShape();
        s.Spec.Config["includeGerbil"] = true;
        var compose = PangolinProvisioner.BuildComposeYaml(s);

        Assert.Contains("--reachableAt=http://gerbil:3003", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("gerbil:3004", compose, StringComparison.Ordinal);
        // remoteConfig supersedes the deprecated reportBandwidthTo.
        Assert.Contains("--remoteConfig=", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("--reportBandwidthTo", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Pangolin_GerbilImage_IsNewEnoughToRegisterPeersWithPangolin()
    {
        // Gerbil registers each WireGuard peer with Pangolin, and since gerbil 1.3.0
        // ("Include public key in hole punch message to Pangolin") that call carries a
        // publicKey. Pangolin ee-1.19.4 REQUIRES it and 400s without it, so an older gerbil
        // silently orphans every site connector — the peer is added to WireGuard locally,
        // Pangolin never learns it exists, and newt waits on newt/wg/get-config forever.
        //
        // Nothing upstream couples the two versions: the installer injects whatever the
        // latest gerbil tag is at build time. So this test is the coupling — it fails if a
        // future edit drops the gerbil pin back below the publicKey release (#455).
        var version = PangolinProvisioner.DefaultGerbilImage.Split(':')[^1];
        Assert.True(Version.Parse(version) >= new Version(1, 3, 0),
            $"gerbil {version} predates the publicKey hole-punch field (1.3.0); "
            + "Pangolin will reject every peer registration with a 400.");

        // And the pin must actually reach the compose file, not just the constant.
        var s = PangolinWildcardShape();
        s.Spec.Config["includeGerbil"] = true;
        Assert.Contains($"image: {PangolinProvisioner.DefaultGerbilImage}",
            PangolinProvisioner.BuildComposeYaml(s), StringComparison.Ordinal);
    }

    [Fact]
    public void Pangolin_GerbilImage_IsOverridableForABumpWithoutAnEngineChange()
    {
        var s = PangolinWildcardShape();
        s.Spec.Config["includeGerbil"] = true;
        s.Spec.Config["gerbilImage"] = "fosrl/gerbil:9.9.9";

        Assert.Contains("image: fosrl/gerbil:9.9.9",
            PangolinProvisioner.BuildComposeYaml(s), StringComparison.Ordinal);
    }

    [Fact]
    public void Pangolin_DockerDeploy_RestartsAfterRenderingConfig()
    {
        // `docker compose up -d` only recreates a service whose DEFINITION changed. config.yml
        // and the Traefik configs are bind-mounted FILES, so a config-only change lands on disk
        // while the running process keeps serving the old value — and converge reports APPLIED.
        // Seen live: base_endpoint rendered to a new value with every container still `Up 3 days`.
        var s = PangolinWildcardShape();
        var script = PangolinProvisioner.BuildDockerDeploy(s, "m", "pangolin.chrison.dev",
            "https://pangolin.chrison.dev", "chrison.dev", "cf-token");

        var up = script.IndexOf("docker compose up -d", StringComparison.Ordinal);
        var restart = script.IndexOf("docker compose restart", StringComparison.Ordinal);
        var marker = script.IndexOf("printf '%s' 'm'", StringComparison.Ordinal);

        Assert.True(up > 0, "the deploy must bring the stack up");
        Assert.True(restart > up, "the restart must follow `up -d`, so rendered config is re-read");
        Assert.True(marker > restart, "the marker must be stamped only after the restart");
    }

    [Fact]
    public void Pangolin_Marker_ChangesWhenGerbilIsTurnedOn()
    {
        // Regression guard (#406). The marker enumerated `gerbilImage` but NOT `includeGerbil`
        // — so turning gerbil ON changed the rendered compose (gerbil added; traefik moved to
        // network_mode: service:gerbil) while the marker stayed put, and the apply reported
        // NOCHANGE. Desired state moved and nothing was applied.
        var before = PangolinWildcardShape();
        var after = PangolinWildcardShape();
        after.Spec.Config["includeGerbil"] = true;

        Assert.NotEqual(PangolinProvisioner.DesiredMarker(before), PangolinProvisioner.DesiredMarker(after));
    }

    [Fact]
    public void Pangolin_Marker_TracksTheRenderedCompose_NotJustAnAllowlistOfFields()
    {
        // The structural fix behind the test above: the marker hashes the rendered artefacts,
        // so ANY input that changes what lands on the host moves it — including one added
        // later and forgotten here. Asserted through a field deliberately NOT enumerated in
        // the marker's explicit list.
        var before = PangolinWildcardShape();
        var after = PangolinWildcardShape();
        after.Spec.Config["includeGerbil"] = true;

        Assert.NotEqual(PangolinProvisioner.BuildComposeYaml(before), PangolinProvisioner.BuildComposeYaml(after));
        Assert.Contains("network_mode: service:gerbil", PangolinProvisioner.BuildComposeYaml(after));
        Assert.DoesNotContain("gerbil", PangolinProvisioner.BuildComposeYaml(before));
    }

    [Fact]
    public void Pangolin_WildcardARecords_OnePerZone_ToPublicIp_WhenPublicWildcard()
    {
        var s = PangolinWildcardShape();
        s.Spec.Config["publicIp"] = "203.0.113.7";
        var recs = PangolinProvisioner.WildcardARecords(s);
        Assert.Equal(2, recs.Count);
        Assert.Contains(("*.arr.chrison.dev", "203.0.113.7"), recs);
        Assert.Contains(("*.lab.chrison.dev", "203.0.113.7"), recs);
    }

    [Fact]
    public void Pangolin_WildcardARecords_Empty_WhenPublicIpUnset_OrCloudflaredEdge()
    {
        // publicIp unset → nothing to declare (the reconcile reports a skip, never an A record).
        Assert.Empty(PangolinProvisioner.WildcardARecords(PangolinWildcardShape()));

        // cloudflared edge has no public :443, so no grey-cloud A records even with publicIp set.
        var cf = PangolinShape();
        cf.Spec.Config["publicIp"] = "203.0.113.7";
        Assert.Empty(PangolinProvisioner.WildcardARecords(cf));
    }

    [Theory]
    [InlineData(null, true)]      // unset → gated (the security default; API create leaves it OPEN, #238)
    [InlineData(true, true)]
    [InlineData(false, false)]    // explicit opt-out for native clients (Plex/abs)
    [InlineData("true", true)]    // YAML scalars can arrive as strings
    [InlineData("false", false)]
    public void Pangolin_Resource_SsoDefaultsOn_UnlessOptedOut(object? sso, bool expected)
    {
        var rd = new Dictionary<object, object> { ["subdomain"] = "x", ["zone"] = "lab" };
        if (sso is not null) rd["sso"] = sso;
        Assert.Equal(expected, PangolinProvisioner.ResourceSsoEnabled(rd));
    }

    [Fact]
    public void Pangolin_ComposeYaml_PinsEeImage_TraefikPublishesPorts_NoGerbilByDefault()
    {
        var compose = PangolinProvisioner.BuildComposeYaml(PangolinWildcardShape());
        // Assert the pin reaches the compose, not the specific version — otherwise every
        // deliberate bump fails a test that was only ever checking that it IS pinned.
        Assert.Contains($"image: {PangolinProvisioner.DefaultImage}", compose);
        Assert.Contains($"image: {PangolinProvisioner.DefaultTraefikImage}", compose);
        // The durable invariant: it must be the ENTERPRISE image. EE is what carries
        // OIDC/RBAC; the OSS image cannot activate a license (#168), so a silent drop to a
        // non-ee tag would take SSO down for every gated resource.
        Assert.Contains("fosrl/pangolin:ee-", compose, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:3003:3003", compose);   // integration API on CT-localhost (resource reconcile)
        Assert.Contains("- 443:443", compose);          // traefik publishes :443 itself
        Assert.Contains("http://localhost:3001/api/v1/", compose); // pangolin healthcheck
        Assert.DoesNotContain("gerbil", compose);       // WireGuard off by default
        Assert.DoesNotContain("network_mode", compose);
    }

    [Fact]
    public void Pangolin_ComposeYaml_IncludesGerbilTopology_WhenOptedIn()
    {
        var s = PangolinWildcardShape();
        s.Spec.Config["includeGerbil"] = true;
        var compose = PangolinProvisioner.BuildComposeYaml(s);
        Assert.Contains("gerbil:", compose);
        Assert.Contains("network_mode: service:gerbil", compose); // traefik shares gerbil's netns
        Assert.Contains("- 51820:51820/udp", compose);
    }

    [Fact]
    public void Pangolin_TraefikStatic_UsesDnsChallengeWildcards_NotHttpChallenge()
    {
        var s = PangolinWildcardShape();
        var st = PangolinProvisioner.BuildTraefikStatic(s, "chrison.dev");
        Assert.Contains("dnsChallenge:", st);
        Assert.Contains("provider: cloudflare", st);
        Assert.Contains("sans: [\"*.arr.chrison.dev\"]", st);
        Assert.Contains("sans: [\"*.lab.chrison.dev\"]", st);
        Assert.Contains("github.com/fosrl/badger", st);      // auth plugin loaded
        Assert.Contains("acme-v02.api.letsencrypt.org", st); // PROD CA by default
        Assert.DoesNotContain("httpChallenge", st);          // wildcards need DNS-01

        s.Spec.Config["leStaging"] = true;
        Assert.Contains("acme-staging-v02", PangolinProvisioner.BuildTraefikStatic(s, "chrison.dev"));
    }

    [Fact]
    public void Pangolin_TraefikDynamic_IsDashboardOnly_PlainHttp_NoBadgerLoop()
    {
        var dyn = PangolinProvisioner.BuildTraefikDynamic("pangolin.chrison.dev");
        Assert.Contains("Host(`pangolin.chrison.dev`)", dyn);
        Assert.Contains("http://pangolin:3002", dyn);   // reaches app by service name
        Assert.Contains("- web", dyn);                  // dashboard on plain :80 (core tunnel fronts it)
        Assert.DoesNotContain("websecure", dyn);
        Assert.DoesNotContain("badger", dyn);           // no auth loop on the login UI
        Assert.DoesNotContain("certResolver", dyn);
    }

    [Fact]
    public void Pangolin_DockerDeploy_RendersArtifacts_AndRunsCompose()
    {
        var cmd = PangolinProvisioner.BuildDockerDeploy(
            PangolinWildcardShape(), "abc123", "pangolin.chrison.dev",
            "https://pangolin.chrison.dev", "chrison.dev", "cf-token-xyz");
        Assert.Contains("docker compose up -d", cmd);
        Assert.Contains("get.docker.com", cmd);               // installs Docker on the plain debian CT
        Assert.Contains("command -v docker", cmd);            // idempotent guard
        Assert.Contains("base64 -d > compose.yml", cmd);
        Assert.Contains("base64 -d > config/traefik/traefik_config.yml", cmd);
        Assert.Contains("base64 -d > .env", cmd);
        Assert.Contains("openssl rand", cmd);                  // server.secret generate-or-preserve
        Assert.Contains("# homelab-managed: abc123", cmd);     // config.yml marker (heredoc, not base64)
        Assert.Contains("cert_resolver: \"letsencrypt\"", cmd);
        // mark-on-SUCCESS: the marker file is the LAST line, after `docker compose up -d`
        var composeUp = cmd.IndexOf("docker compose up -d", StringComparison.Ordinal);
        var markerWrite = cmd.IndexOf("/opt/pangolin/.homelab-managed", StringComparison.Ordinal);
        Assert.True(composeUp >= 0 && markerWrite > composeUp, "marker must be written after compose up");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("42", 42L)]
    [InlineData("http://x", "http://x")]
    public void CoerceScalar_MapsYamlStringsToJsonTypes(string input, object expected)
    {
        // YamlDotNet hands scalars as strings; noTLSVerify:true must serialize as a bool,
        // not "true" (else Cloudflare's ingress push 400s with code 1056).
        Assert.Equal(expected, CloudflaredProvisioner.CoerceScalar(input));
    }

    // ---- CloudflaredProvisioner declarative reconcile / prune (#195) -------

    private const string Managed = CloudflareApi.ManagedComment;
    private const string TunnelId = "11111111-2222-3333-4444-555555555555";
    private static string Content(string id) => $"{id}.cfargotunnel.com";
    private static IReadOnlySet<string> Hosts(params string[] h) =>
        new HashSet<string>(h, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void CnamesToPrune_RemovesOurOrphans_KeepsShapeHosts()
    {
        var live = new[]
        {
            new CfDnsRecord("r1", "seerr.chrison.dev", Content(TunnelId), Managed),          // in shape → keep
            new CfDnsRecord("r2", "prowlarr.chrison.dev", Content(TunnelId), Managed),       // ours, gone from shape → prune
        };
        var pruned = CloudflaredProvisioner.CnamesToPrune(live, TunnelId, Hosts("seerr.chrison.dev"));
        Assert.Equal(new[] { "prowlarr.chrison.dev" }, pruned.Select(r => r.Name));
    }

    [Fact]
    public void CnamesToPrune_NeverTouchesHandManagedOrOtherTunnels()
    {
        var other = "99999999-0000-0000-0000-000000000000";
        var live = new[]
        {
            new CfDnsRecord("r1", "hand.chrison.dev", Content(TunnelId), ""),                // no managed comment → leave
            new CfDnsRecord("r2", "blog.chrison.dev", "some.other.host", "managed by hand"), // not our comment → leave
            new CfDnsRecord("r3", "moved.chrison.dev", Content(other), Managed),             // ours but points at ANOTHER tunnel → leave
        };
        // shape has no hosts: everything would be an orphan if scoping were wrong.
        var pruned = CloudflaredProvisioner.CnamesToPrune(live, TunnelId, Hosts());
        Assert.Empty(pruned);
    }

    [Fact]
    public void AccessAppsToPrune_RemovesOurDegatedApps_BySuffix()
    {
        var live = new[]
        {
            new CfAccessApp("a1", "seerr (Media)", "seerr.chrison.dev"),         // still gated → keep
            new CfAccessApp("a2", "prowlarr (Media)", "prowlarr.chrison.dev"),   // ours, no longer gated → prune
            new CfAccessApp("a3", "audiobookshelf (Media)", "audiobookshelf.chrison.dev"), // flipped public (ungated) → prune
            new CfAccessApp("a4", "pdm", "pdm.chrison.dev"),                     // hand-managed (no suffix) → keep
            new CfAccessApp("a5", "traefik (Core)", "traefik.chrison.dev"),      // another stack's suffix → keep
        };
        var pruned = CloudflaredProvisioner.AccessAppsToPrune(live, " (Media)", Hosts("seerr.chrison.dev"));
        Assert.Equal(new[] { "prowlarr.chrison.dev", "audiobookshelf.chrison.dev" }, pruned.Select(a => a.Domain));
    }

    // ---- CF Access trusted-IP bypass reconcile (#417) ---------------------
    // The policy is matched by name, so existence alone used to end the story and an
    // edited `access.bypass` never reached Cloudflare. These pin the drift decision.

    [Fact]
    public void BypassDrifted_DetectsAMissingAddressFamily()
    {
        // The actual bug: live carried only the IPv4 home address while the shape had
        // grown the IPv6 prefix, so every dual-stack browser hit the OTP gate.
        var live = new[] { "203.0.113.7/32" };
        var desired = new[] { "203.0.113.7/32", "2001:db8:116d:e500::/56" };
        Assert.True(CloudflaredProvisioner.BypassDrifted(live, desired));
    }

    [Fact]
    public void BypassDrifted_IsFalseWhenOnlyOrderOrSpellingDiffers()
    {
        // Cloudflare echoes back its own normalisation, and order is not meaningful.
        // Treating either as drift would rewrite the policy on every single converge.
        var live = new[] { "2001:DB8:116D:E500:0:0:0:0/56", "203.0.113.7/32" };
        var desired = new[] { "203.0.113.7/32", "2001:db8:116d:e500::/56" };
        Assert.False(CloudflaredProvisioner.BypassDrifted(live, desired));
    }

    [Fact]
    public void BypassDrifted_DetectsAWidenedOrNarrowedPrefix()
    {
        // /48 is Quic's pool, not the house — widening to it must never look like a no-op.
        Assert.True(CloudflaredProvisioner.BypassDrifted(
            new[] { "2001:db8:116d:e500::/56" }, new[] { "2001:db8:116d::/48" }));
    }

    [Fact]
    public void BypassDrifted_DetectsARemovedEntryAndAnEmptyLivePolicy()
    {
        Assert.True(CloudflaredProvisioner.BypassDrifted(
            new[] { "203.0.113.7/32", "203.0.113.5/32" }, new[] { "203.0.113.7/32" }));
        Assert.True(CloudflaredProvisioner.BypassDrifted(
            Array.Empty<string>(), new[] { "203.0.113.7/32" }));
    }

    [Fact]
    public void BypassDrifted_KeepsUnparseableEntriesDistinct()
    {
        // A garbage entry must count as a difference, not normalise away to nothing and
        // silently compare equal — that would leave a broken policy in place forever.
        Assert.True(CloudflaredProvisioner.BypassDrifted(
            new[] { "not-an-ip" }, new[] { "203.0.113.7/32" }));
        Assert.False(CloudflaredProvisioner.BypassDrifted(
            new[] { "not-an-ip" }, new[] { "not-an-ip" }));
    }

    // #322 — a tunnel may front hostnames in more than one zone (the household media path
    // is tao-simon.family while the rest of the stack is chrison.dev). Every DNS call used
    // to be made against ingress[0]'s zone, which silently misfiled second-zone records.
    [Theory]
    [InlineData("seerr.chrison.dev", "chrison.dev")]
    [InlineData("seerr.tao-simon.family", "tao-simon.family")]
    [InlineData("audiobookshelf.tao-simon.family", "tao-simon.family")]
    [InlineData("a.b.c.chrison.dev", "chrison.dev")]
    [InlineData("chrison.dev", "chrison.dev")]          // apex — already only two labels
    [InlineData("localhost", "localhost")]              // degenerate, must not throw
    public void ZoneNameOf_ReturnsRegistrableDomain(string host, string expected)
        => Assert.Equal(expected, CloudflaredProvisioner.ZoneNameOf(host));

    [Fact]
    public void ZoneNameOf_GroupsMixedZoneIngressCorrectly()
    {
        var hosts = new[] { "seerr.chrison.dev", "seerr.tao-simon.family", "audiobookshelf.chrison.dev" };
        var zones = hosts.Select(CloudflaredProvisioner.ZoneNameOf)
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(z => z).ToArray();
        Assert.Equal(new[] { "chrison.dev", "tao-simon.family" }, zones);
    }

    // Walk up from the test assembly to a repo-relative file, the same way AppCatalogueTests
    // locates the catalogue — the test binary does not sit at the repo root.
    // A SecretsEnv carrying the home-edge variables, from a throwaway file — the same
    // code path production uses, with documentation-range values.
    private static SecretsEnv StubHomeEdgeVars()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"secrets-{Guid.NewGuid():N}.env");
        File.WriteAllText(tmp, "HOME_WAN_IP=203.0.113.7\nHOME_WAN_IPV6_PREFIX=2001:db8:116d:e500::\n");
        try { return SecretsEnv.Load(tmp); }
        finally { File.Delete(tmp); }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} by walking up from {AppContext.BaseDirectory}");
    }

    // ── Identity providers (#468) ─────────────────────────────────────────────────────

    private static Dictionary<object, object> AuthentikIdpEntry() => new()
    {
        ["name"] = "Authentik",
        ["authUrl"] = "https://identity.chrison.dev/application/o/authorize/",
        ["tokenUrl"] = "https://identity.chrison.dev/application/o/token/",
        ["clientIdFrom"] = "AUTHENTIK_PANGOLIN_CLIENT_ID",
        ["clientSecretFrom"] = "AUTHENTIK_PANGOLIN_CLIENT_SECRET",
        ["identifierPath"] = "sub",
        ["emailPath"] = "email",
        ["namePath"] = "preferred_username",
        ["scopes"] = "openid profile email",
        ["autoProvision"] = true,
        ["roleMapping"] = "contains(groups, 'Homelab Admins') && 'Admin' || 'Member'",
    };

    [Fact]
    public void Pangolin_DeclaredIdps_CarriesSecretKeyNamesRatherThanSecretValues()
    {
        // The shape must never hold a credential — it names the secrets.env KEYS and the
        // reconciler resolves them. This is what lets the same two keys configure BOTH ends of
        // the OIDC relationship (Authentik's blueprint reads them via `!Env`) without either
        // side's value ever being copied out of a UI or committed.
        var s = PangolinWildcardShape();
        s.Spec.Config["idps"] = new List<object> { AuthentikIdpEntry() };

        var idps = PangolinProvisioner.DeclaredIdps(s);

        var idp = Assert.Single(idps);
        Assert.Equal("Authentik", idp.Name);
        Assert.Equal("AUTHENTIK_PANGOLIN_CLIENT_ID", idp.ClientIdFrom);
        Assert.Equal("AUTHENTIK_PANGOLIN_CLIENT_SECRET", idp.ClientSecretFrom);
    }

    [Fact]
    public void Pangolin_DeclaredIdps_DefaultsTheClaimPathsSoAMinimalEntryStillWorks()
    {
        var s = PangolinWildcardShape();
        s.Spec.Config["idps"] = new List<object>
        {
            new Dictionary<object, object>
            {
                ["name"] = "Authentik",
                ["authUrl"] = "https://identity.chrison.dev/application/o/authorize/",
                ["tokenUrl"] = "https://identity.chrison.dev/application/o/token/",
            },
        };

        var idp = Assert.Single(PangolinProvisioner.DeclaredIdps(s));

        Assert.Equal("sub", idp.IdentifierPath);
        Assert.Equal("email", idp.EmailPath);
        Assert.Equal("preferred_username", idp.NamePath);
        Assert.Equal("openid profile email", idp.Scopes);
        Assert.True(idp.AutoProvision);
    }

    [Fact]
    public void Pangolin_DeclaredIdps_SkipsAnEntryMissingItsEndpoints()
    {
        // A half-written entry must not become a half-configured IdP. Pangolin requires both
        // URLs, so an entry without them is dropped rather than sent and rejected at the API.
        var s = PangolinWildcardShape();
        s.Spec.Config["idps"] = new List<object>
        {
            new Dictionary<object, object> { ["name"] = "Broken" },
            AuthentikIdpEntry(),
        };

        Assert.Equal("Authentik", Assert.Single(PangolinProvisioner.DeclaredIdps(s)).Name);
    }

    [Fact]
    public void Pangolin_IdpOidcJson_SendsTheFieldsPangolinRequires()
    {
        var idp = Assert.Single(IdpsFrom(AuthentikIdpEntry()));

        var json = JsonDocument.Parse(PangolinProvisioner.IdpOidcJson(idp, "the-id", "the-secret")).RootElement;

        Assert.Equal("Authentik", json.GetProperty("name").GetString());
        Assert.Equal("the-id", json.GetProperty("clientId").GetString());
        Assert.Equal("the-secret", json.GetProperty("clientSecret").GetString());
        Assert.Equal("oidc", json.GetProperty("variant").GetString());
        Assert.Equal("openid profile email", json.GetProperty("scopes").GetString());
        Assert.True(json.GetProperty("autoProvision").GetBoolean());
    }

    [Fact]
    public void Pangolin_IdpDrifted_NoticesAChangedIssuerButIgnoresTheUnreadableSecret()
    {
        // The drift check is the #309 lesson applied to identity: an authUrl left pointing at a
        // stale issuer is a login loop, and add-only reconciliation would report a clean plan
        // while it stayed broken. The client secret is deliberately NOT compared — the API
        // never reads one back, so "differs" is unknowable and would force an update every run.
        var idp = Assert.Single(IdpsFrom(AuthentikIdpEntry()));
        var live = JsonDocument.Parse("""
            {"idpId":1,"name":"Authentik",
             "authUrl":"https://identity.chrison.dev/application/o/authorize/",
             "tokenUrl":"https://identity.chrison.dev/application/o/token/",
             "identifierPath":"sub","emailPath":"email","namePath":"preferred_username",
             "scopes":"openid profile email","autoProvision":1}
            """).RootElement;

        Assert.False(PangolinProvisioner.IdpDrifted(live, idp));

        var moved = JsonDocument.Parse("""
            {"idpId":1,"name":"Authentik",
             "authUrl":"https://old-idp.example/authorize/",
             "tokenUrl":"https://identity.chrison.dev/application/o/token/",
             "identifierPath":"sub","emailPath":"email","namePath":"preferred_username",
             "scopes":"openid profile email","autoProvision":1}
            """).RootElement;

        Assert.True(PangolinProvisioner.IdpDrifted(moved, idp));
    }

    [Fact]
    public void ShapeValidator_SkipsAssetPayloadsSoBlueprintTagsDoNotTripIt()
    {
        // Authentik blueprints are valid YAML that no general parser can resolve, because of
        // the `!Env` / `!Find` / `!KeyOf` tags. They live under assets/ and are payloads, not
        // shapes. The apiVersion probe deliberately KEEPS unparseable files so a typo in a real
        // shape is never silently skipped — so asset trees have to be excluded before it.
        Assert.True(ShapeValidator.IsAssetPayload("stacks/Core/authentik/assets/blueprints/00-homelab-identity.yaml"));
        Assert.True(ShapeValidator.IsAssetPayload("stacks/Monitoring/podman-host/assets/prometheus/prometheus.yaml"));
        Assert.False(ShapeValidator.IsAssetPayload("stacks/Core/authentik.lxc.yaml"));
        Assert.False(ShapeValidator.IsAssetPayload("stacks/Core/pangolin.lxc.yaml"));
    }

    [Fact]
    public void Pangolin_IdpDrifted_IsNotFooledByTheListingPayload()
    {
        // THE BUG THIS LOCKS DOWN. GET /idp returns only {idpId, name, type, variant,
        // orgCount, autoProvision, tags} — no authUrl, tokenUrl, scopes or claim paths. The
        // first version of the reconciler compared the declared values against THAT, so every
        // absent field read as "" != desired, drift was true on every run, an update fired,
        // and the update 403s on an org-scoped key — failing the whole apply and taking the
        // resource reconcile down with it, on a Pangolin whose IdP was already correct.
        //
        // The listing shape is asserted here so a future change that starts diffing against it
        // again fails loudly instead of in production.
        var idp = Assert.Single(IdpsFrom(AuthentikIdpEntry()));
        var listing = JsonDocument.Parse("""
            {"idpId":1,"name":"Authentik","type":"oidc","variant":"oidc","orgCount":1,
             "autoProvision":true,"tags":null}
            """).RootElement;

        Assert.False(listing.TryGetProperty("authUrl", out _));
        Assert.False(listing.TryGetProperty("scopes", out _));
        // Against the listing everything looks drifted — which is precisely why the listing
        // must never be the comparison source.
        Assert.True(PangolinProvisioner.IdpDrifted(listing, idp));
    }

    [Fact]
    public void Pangolin_IdpDrifted_ComparesCleanlyAgainstTheDetailPayload()
    {
        // The same IdP, read from the per-IdP endpoint that actually carries the fields,
        // compares equal. That endpoint is root-scoped, so with an org key the reconciler
        // reports "drift NOT checked" rather than guessing — but when a root key IS present
        // this is the comparison it makes.
        var idp = Assert.Single(IdpsFrom(AuthentikIdpEntry()));
        var detail = JsonDocument.Parse("""
            {"idpId":1,"name":"Authentik",
             "authUrl":"https://identity.chrison.dev/application/o/authorize/",
             "tokenUrl":"https://identity.chrison.dev/application/o/token/",
             "identifierPath":"sub","emailPath":"email","namePath":"preferred_username",
             "scopes":"openid profile email","autoProvision":true}
            """).RootElement;

        Assert.False(PangolinProvisioner.IdpDrifted(detail, idp));
    }

    // ── verify steps (#485) ───────────────────────────────────────────────────────────

    [Fact]
    public void Verify_SubstitutesSinceSoAStaleSuccessCannotPass()
    {
        // The token is what separates "applied by this run" from "was already successful".
        // A blueprint that failed today but succeeded yesterday still carries
        // status='successful', so a check without the timestamp reads as a pass — the exact
        // stale-read this mechanism exists to stop.
        const string run = "psql -c \"select 1 where last_applied < '{{since}}'\"";

        var rendered = PodmanProvisioner.RenderVerify(run, "2026-08-23 07:15:35");

        Assert.Contains("last_applied < '2026-08-23 07:15:35'", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{{since}}", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_OnANoChangeRunAsksTheWeakerQuestionRatherThanNone()
    {
        // A no-change run cannot assert "applied by this run" — it applied nothing. Feeding
        // epoch makes the freshness clause vacuously true, leaving "is it successful right
        // now", which is the strongest thing that path can honestly claim. Skipping the check
        // entirely was the original hole: a blueprint rejected on the run that shipped it
        // stays rejected, and every later converge reports a contented NOCHANGE forever.
        const string run = "psql -c \"select 1 where status <> 'successful' or last_applied < '{{since}}'\"";

        var rendered = PodmanProvisioner.RenderVerify(run, PodmanProvisioner.EpochSince);

        Assert.Contains("last_applied < '1970-01-01 00:00:00+00'", rendered, StringComparison.Ordinal);
        Assert.Contains("status <> 'successful'", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_SinceCarriesAnExplicitUtcOffset()
    {
        // The stamp is taken with `date -u`, but UTC that does not SAY it is UTC is resolved
        // by the consumer in the consumer's timezone. Postgres parses an offset-less literal
        // against the session TimeZone, and the authentik container runs TZ=Pacific/Auckland,
        // so the stamp lands ~12 hours early and `last_applied < since` is false for anything
        // applied today. The clause stops firing and the check quietly weakens to "is it
        // successful right now" — which is how a converge passed verify over a blueprint it
        // had shipped but authentik had not yet applied (#539).
        //
        // Asserted on the constant because the live stamp is produced by a shell call: if the
        // two ever diverge in shape, this is the cheaper place to notice.
        Assert.EndsWith("+00", PodmanProvisioner.EpochSince, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_StepsCarryTheirOwnRetryBudget()
    {
        var s = Lxc("authentik", "2014");
        s.Spec.Config["verify"] = new List<object>
        {
            new Dictionary<object, object>
            {
                ["name"] = "blueprints applied", ["run"] = "psql -c 'select 1'",
                ["retries"] = 18, ["intervalSeconds"] = 10,
            },
        };

        var step = Assert.Single(PodmanProvisioner.VerifySteps(s));

        Assert.Equal("blueprints applied", step.Name);
        Assert.Equal(18, step.Retries);
        Assert.Equal(10, step.IntervalSeconds);
    }

    [Fact]
    public void Verify_DefaultsAreGenerousBecauseTheThingBeingCheckedIsAsynchronous()
    {
        // authentik discovers blueprints on a timer and a first apply waits on migrations, so
        // a single immediate check would fail a converge that is merely early.
        var s = Lxc("authentik", "2014");
        s.Spec.Config["verify"] = new List<object>
        {
            new Dictionary<object, object> { ["run"] = "true" },
        };

        var step = Assert.Single(PodmanProvisioner.VerifySteps(s));

        Assert.True(step.Retries * step.IntervalSeconds >= 60,
            "a verify step's default budget must tolerate an asynchronous consumer");
    }

    [Fact]
    public void Verify_AnEntryWithNoCommandIsDroppedRatherThanRunAsAnEmptyPass()
    {
        // A half-written entry must not become a check that trivially succeeds — that would be
        // worse than no check, because the plan would claim verification happened.
        var s = Lxc("authentik", "2014");
        s.Spec.Config["verify"] = new List<object>
        {
            new Dictionary<object, object> { ["name"] = "forgot the command" },
            new Dictionary<object, object> { ["name"] = "real", ["run"] = "psql -c 'select 1'" },
        };

        Assert.Equal("real", Assert.Single(PodmanProvisioner.VerifySteps(s)).Name);
    }

    private static IReadOnlyList<PangolinProvisioner.DeclaredIdp> IdpsFrom(Dictionary<object, object> entry)
    {
        var s = PangolinWildcardShape();
        s.Spec.Config["idps"] = new List<object> { entry };
        return PangolinProvisioner.DeclaredIdps(s);
    }
    // ── Forgejo OIDC (#485) ───────────────────────────────────────────────────────────

    private static Shape ForgejoOidcShape()
    {
        var s = Lxc("forgejo", "3000");
        s.Spec.Node = "desktop-01";
        s.Spec.Config["rootUrl"] = "https://forgejo.chrison.dev";
        s.Spec.Config["oidc"] = new Dictionary<object, object>
        {
            ["name"] = "authentik",
            ["discoveryUrl"] = "https://identity.chrison.dev/application/o/forgejo/.well-known/openid-configuration",
            ["clientIdFrom"] = "AUTHENTIK_FORGEJO_CLIENT_ID",
            ["clientSecretFrom"] = "AUTHENTIK_FORGEJO_CLIENT_SECRET",
            ["groupClaim"] = "groups",
            ["adminGroup"] = "homelab-admins",
        };
        return s;
    }

    [Fact]
    public void Forgejo_ScopesArePassedAsRepeatedFlagsNotOneJoinedString()
    {
        // `--scopes` is declared as a string SLICE. Passing "openid profile email" as a single
        // value stores one scope literally named that, so the request asks for a scope nobody
        // defines and silently gets none of the three — including `profile`, which is the one
        // carrying the groups claim the admin mapping depends on.
        var o = PangolinForgejoOidc();

        var cmd = ForgejoProvisioner.BuildAuthCommand(o, "the-id", "the-secret", null);

        Assert.Contains("--scopes 'openid'", cmd, StringComparison.Ordinal);
        Assert.Contains("--scopes 'profile'", cmd, StringComparison.Ordinal);
        Assert.Contains("--scopes 'email'", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("--scopes 'openid profile email'", cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void Forgejo_ExistingSourceIsUpdatedInPlaceRatherThanDuplicated()
    {
        var o = PangolinForgejoOidc();

        var create = ForgejoProvisioner.BuildAuthCommand(o, "id", "secret", null);
        var update = ForgejoProvisioner.BuildAuthCommand(o, "id", "secret", 3);

        Assert.Contains("auth add-oauth", create, StringComparison.Ordinal);
        Assert.Contains("auth update-oauth --id 3", update, StringComparison.Ordinal);
        Assert.DoesNotContain("add-oauth", update, StringComparison.Ordinal);
    }

    [Fact]
    public void Forgejo_ParsesTheSourceIdOutOfAuthListAndIgnoresOtherSources()
    {
        // `auth list` is tab-separated with a header row. Matching on the name column rather
        // than a substring matters: "authentik" must not match "authentik-old".
        const string listing = "ID\tName\tType\tEnabled\n1\tldap-legacy\tLDAP\ttrue\n7\tauthentik\tOAuth2\ttrue\n";

        Assert.Equal(7, ForgejoProvisioner.ParseAuthSourceId(listing, "authentik"));
        Assert.Null(ForgejoProvisioner.ParseAuthSourceId(listing, "authentik-old"));
        Assert.Null(ForgejoProvisioner.ParseAuthSourceId("ID\tName\tType\tEnabled\n", "authentik"));
    }

    [Fact]
    public void Forgejo_AdminGroupIsOmittedRatherThanSentEmpty()
    {
        // An empty --admin-group would map the empty group to admin, which every user without
        // the claim would arguably match. Omit the flag instead of sending a blank one.
        var s = ForgejoOidcShape();
        ((Dictionary<object, object>)s.Spec.Config["oidc"]!)["adminGroup"] = "";

        var cmd = ForgejoProvisioner.BuildAuthCommand(
            ForgejoProvisioner.OidcSource(s)!.Value, "id", "secret", null);

        Assert.DoesNotContain("--admin-group", cmd, StringComparison.Ordinal);
    }

    private static ForgejoProvisioner.ForgejoOidc PangolinForgejoOidc() =>
        ForgejoProvisioner.OidcSource(ForgejoOidcShape())!.Value;

}
