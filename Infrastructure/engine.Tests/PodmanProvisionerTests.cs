using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// PodmanProvisioner unit tests (ADR-0009 / #284). Pure + faked INodeExec — no live cluster.
// The live acceptance checks (a throwaway CT, a hello-world quadlet surviving a reboot) are
// deliberately out of scope here; these lock down the parts that are cheap to get wrong:
// idempotency, the nested-userns guard, and the rootless/socket posture.
public sealed class PodmanProvisionerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    // A shape whose SourceDir is a real temp stack dir, optionally holding quadlet files.
    private Shape PodmanShape(params (string Name, string Content)[] quadlets)
    {
        var stackDir = Path.Combine(Path.GetTempPath(), "podman-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stackDir);
        _tempDirs.Add(stackDir);

        var s = new Shape { Metadata = new ShapeMetadata { Name = "mate" }, SourceDir = stackDir };
        s.Spec.Node = "pve1";
        s.Spec.Ctid = "4100";
        s.Spec.App = "podman";

        if (quadlets.Length > 0)
        {
            var qdir = Path.Combine(stackDir, "mate", "quadlets");
            Directory.CreateDirectory(qdir);
            foreach (var (name, content) in quadlets)
                File.WriteAllText(Path.Combine(qdir, name), content);
        }
        return s;
    }

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

    private static ConvergeContext Ctx(INodeExec exec, SecretsEnv? secrets = null) =>
        new(exec, secrets ?? SecretsEnv.Load(null), new Dictionary<string, Shape>(), Deriver: null!);

    // ---- registry wiring --------------------------------------------------

    [Fact]
    public void Registry_DispatchesPodmanApp_ToPodmanProvisioner()
    {
        Assert.IsType<PodmanProvisioner>(ProvisionerRegistry.Default().For("podman"));
    }

    // ---- idempotency ------------------------------------------------------

    [Fact]
    public async Task ReportsNoChange_WhenMarkerAlreadyMatches()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=docker.io/leapmotor/mate:1\n"));
        var marker = PodmanProvisioner.DesiredMarker(shape);

        // The marker read-back returns the desired marker → nothing may mutate.
        var exec = new FakeNodeExec(cmd =>
            cmd.Contains(".homelab-managed")
                ? new ExecResult(0, marker, "")
                : throw new InvalidOperationException($"unexpected mutating command: {cmd}"));

        var result = await new PodmanProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        Assert.Single(exec.Commands);   // only the read-back ran
    }

    [Fact]
    public async Task Applies_WhenMarkerAbsent()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=docker.io/leapmotor/mate:1\n"));
        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));

        var result = await new PodmanProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains("mate.service", result.Message);
    }

    [Fact]
    public void Marker_ChangesWhenQuadletContentChanges()
    {
        var a = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var b = PodmanShape(("mate.container", "[Container]\nImage=x:2\n"));

        // Content is hashed, not just filenames — editing a quadlet must re-deploy.
        Assert.NotEqual(PodmanProvisioner.DesiredMarker(a), PodmanProvisioner.DesiredMarker(b));
    }

    [Fact]
    public void Marker_CoversTheDeployRecipe_NotJustItsInputs()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));

        // Regression guard for the CT 9900 miss: the marker must fold in the generated script,
        // so a provisioner fix re-converges instead of reporting NOCHANGE forever on hosts
        // carrying the old marker. Proven by checking a script-only signal is in the hash.
        var withRecipe = PodmanProvisioner.DesiredMarker(shape);
        shape.Spec.Config["autoUpdate"] = "false";   // changes the script, not the quadlets
        Assert.NotEqual(withRecipe, PodmanProvisioner.DesiredMarker(shape));
    }

    [Fact]
    public void Marker_IsStableForIdenticalInputs()
    {
        var a = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var b = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));

        Assert.Equal(PodmanProvisioner.DesiredMarker(a), PodmanProvisioner.DesiredMarker(b));
    }

    // ---- rootless posture (the point of ADR-0009) -------------------------

    [Fact]
    public void Deploy_MasksTheRootPodmanSocket()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var script = Build(shape);

        // podman-install.sh enables a ROOT podman.socket; the rootless model owns no socket.
        Assert.Contains("systemctl disable --now podman.socket", script);
        Assert.Contains("systemctl mask podman.socket", script);
    }

    [Fact]
    public void Deploy_EnablesLingerBeforeAnyUserSystemctl()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var script = Build(shape);

        var linger = script.IndexOf("enable-linger", StringComparison.Ordinal);
        var firstUserCtl = script.IndexOf("systemctl --user", StringComparison.Ordinal);
        Assert.True(linger >= 0 && firstUserCtl >= 0);
        // Without linger first there is no /run/user/<uid> and every --user call fails.
        Assert.True(linger < firstUserCtl, "enable-linger must precede the first systemctl --user");
    }

    [Fact]
    public void Deploy_DrivesUserUnitsWithRuntimeDirAndBus()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var script = Build(shape);

        // pct exec has no login session — both vars are required for `systemctl --user`.
        Assert.Contains("runuser -u podman", script);
        Assert.Contains("XDG_RUNTIME_DIR=/run/user/$UID_N", script);
        Assert.Contains("DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$UID_N/bus", script);
    }

    [Fact]
    public void Deploy_GuardsSubuidRangeAgainstTheContainersOwnUidMap()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var script = Build(shape);

        // The nested-userns trap: the range must fit inside the LXC's own map, and we verify
        // it against /proc/self/uid_map rather than trusting the host convention.
        Assert.Contains("/proc/self/uid_map", script);
        Assert.Contains("does not fit this LXC uid_map", script);
        Assert.Contains("podman:10000:50000", script);
    }

    [Fact]
    public void DefaultSubidRange_FitsTheConventional65536UidWindow()
    {
        var shape = PodmanShape();
        var (start, count) = PodmanProvisioner.SubidRange(shape);
        Assert.True(start + count <= 65536,
            "the default subuid window must fit an unprivileged LXC's 65536-uid map");
    }

    [Fact]
    public void Deploy_ReplacesExistingSubidLine_RatherThanAppending()
    {
        var shape = PodmanShape();
        var script = Build(shape);

        // A changed range must converge, not accumulate a second conflicting entry.
        Assert.Contains("sed -i '/^podman:/d' /etc/subuid", script);
        Assert.Contains("sed -i '/^podman:/d' /etc/subgid", script);
    }

    [Fact]
    public void Deploy_StampsMarkerLast_SoPartialFailuresReRun()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var script = Build(shape);

        Assert.StartsWith("set -e", script);
        var restart = script.IndexOf("systemctl --user restart", StringComparison.Ordinal);
        var stamp = script.IndexOf(".homelab-managed", StringComparison.Ordinal);
        Assert.True(restart < stamp, "the marker must be stamped after the deploy, not before");
    }

    [Fact]
    public void Deploy_MovesToAWorldReadableCwdBeforeRunningAnythingAsTheUser()
    {
        var shape = PodmanShape();
        var script = Build(shape);

        // pct exec lands in /root and runuser keeps the caller's cwd, so podman would fail
        // with "cannot chdir to /root". Found live on CT 9900.
        var cd = script.IndexOf("cd /", StringComparison.Ordinal);
        var firstRunuser = script.IndexOf("runuser", StringComparison.Ordinal);
        Assert.True(cd >= 0 && firstRunuser >= 0);
        Assert.True(cd < firstRunuser, "cd out of /root must precede any runuser call");
    }

    [Fact]
    public void Deploy_MakesNetworkOnlineTargetReachable_BeforeEnablingLinger()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var script = Build(shape);

        // Podman injects Wants=/After=podman-user-wait-network-online.service into every
        // generated unit; that helper spins on `systemctl is-active network-online.target`,
        // which ifupdown never reaches in an LXC → a 92s boot delay before quadlets start
        // (measured live on CT 9900). Fix the cause, don't mask the helper.
        Assert.Contains("homelab-network-online.service", script);
        Assert.Contains("systemctl enable --now homelab-network-online.service", script);

        // The unit body is base64'd into the script, so decode it to assert the ordering that
        // makes it correct rather than merely present.
        var unit = DecodeUnitWrittenTo(script, "/etc/systemd/system/homelab-network-online.service");
        Assert.Contains("After=networking.service", unit);   // ifup blocks on DHCP → real readiness
        Assert.Contains("Wants=network-online.target", unit);
        Assert.Contains("Before=network-online.target", unit);
        Assert.Contains("WantedBy=multi-user.target", unit); // pulled in at boot

        var net = script.IndexOf("homelab-network-online.service", StringComparison.Ordinal);
        var linger = script.IndexOf("enable-linger", StringComparison.Ordinal);
        Assert.True(net < linger, "network-online must be reachable before user units come up");
    }

    [Fact]
    public void Deploy_DoesNotMaskThePodmanNetworkHelper()
    {
        var script = Build(PodmanShape(("mate.container", "[Container]\nImage=x:1\n")));

        // Masking it would also remove the delay but discards the network-readiness guarantee.
        Assert.DoesNotContain("mask podman-user-wait-network-online", script);
    }

    // ---- observe: prune timer + opt-in socket (#283 phase 3) --------------

    [Fact]
    public void Deploy_InstallsAWeeklyPruneTimer_ThatNeverTouchesVolumes()
    {
        var script = Build(PodmanShape(("mate.container", "[Container]\nImage=x:1\n")));

        Assert.Contains("podman-system-prune.timer", script);
        Assert.Contains("OnCalendar=weekly", script);
        Assert.Contains("podman system prune -af --filter until=168h", script);
        // NOT --volumes: an unattached volume may hold a stopped service's database, and
        // losing it to a housekeeping timer would be unrecoverable.
        Assert.DoesNotContain("--volumes", script);
        // User units, so it prunes the rootless store rather than root's.
        Assert.Contains("/home/podman/.config/systemd/user/podman-system-prune.timer", script);
    }

    [Fact]
    public void Deploy_LeavesTheApiSocketOff_UnlessExplicitlyRequested()
    {
        var off = Build(PodmanShape(("mate.container", "[Container]\nImage=x:1\n")));
        // The ROOT socket is masked either way; the point here is that no USER socket appears
        // unless a host actually needs one, so the default posture has no API surface at all.
        Assert.DoesNotContain("enable --now podman.socket", off);
        Assert.Contains("mask podman.socket", off);

        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        shape.Spec.Config["userSocket"] = "true";
        var on = Build(shape);
        Assert.Contains("systemctl --user enable --now podman.socket", on);
        Assert.Contains("mask podman.socket", on);   // root socket STILL masked
    }

    [Fact]
    public void Marker_ChangesWhenTheUserSocketIsToggled()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        var before = PodmanProvisioner.DesiredMarker(shape);
        shape.Spec.Config["userSocket"] = "true";
        Assert.NotEqual(before, PodmanProvisioner.DesiredMarker(shape));
    }

    [Fact]
    public void Deploy_SetsUpPlatformHousekeeping_BeforeStartingAppUnits()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        shape.Spec.Config["userSocket"] = "true";
        var script = Build(shape);

        // Regression guard: when podman-exporter failed to start on CT 4001/5114, `set -e`
        // aborted the script and the prune timer + socket steps never ran at all. Platform
        // housekeeping must not be hostage to an application unit — and a unit that
        // Requires=podman.socket needs the socket enabled before it starts, too.
        var timer = script.IndexOf("podman-system-prune.timer", StringComparison.Ordinal);
        var sock  = script.IndexOf("enable --now podman.socket", StringComparison.Ordinal);
        var start = script.IndexOf("systemctl --user restart", StringComparison.Ordinal);
        Assert.True(timer >= 0 && sock >= 0 && start >= 0);
        Assert.True(timer < start, "prune timer must be installed before app units start");
        Assert.True(sock  < start, "the API socket must be enabled before a unit that Requires= it");
    }

    [Fact]
    public void Deploy_InstallsCockpit_AndUnlocksTheRootlessUserSoItCanLogIn()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        shape.Spec.Config["cockpit"] = "true";
        var script = PodmanProvisioner.BuildDeploy(
            shape, "podman", "m", "/home/podman/.homelab-managed",
            PodmanProvisioner.QuadletFiles(shape), new Dictionary<string, string>(), "s3kret");

        Assert.Contains("cockpit cockpit-podman", script);
        // The SOCKET must be enabled AFTER app units restart: binding :9090 while Prometheus
        // still holds its old published port fails with `Result: resources` (hit on CT 4001).
        var install = script.IndexOf("cockpit cockpit-podman", StringComparison.Ordinal);
        var restart = script.IndexOf("systemctl --user restart", StringComparison.Ordinal);
        var sock    = script.IndexOf("enable --now cockpit.socket", StringComparison.Ordinal);
        Assert.True(install < restart, "install the package before units, so it exists even if one fails");
        Assert.True(restart < sock, "enable cockpit.socket only after units have rebound their ports");
        // The point: rootless containers live only in the `podman` user's session, and both
        // podman and root ship password-LOCKED on a community-scripts CT — so without this
        // Cockpit installs but nobody can log in.
        Assert.Contains("'podman:s3kret' | chpasswd", script);
    }

    [Fact]
    public void Deploy_OmitsCockpit_WhenNotRequested()
    {
        var script = Build(PodmanShape(("mate.container", "[Container]\nImage=x:1\n")));
        Assert.DoesNotContain("cockpit", script);
        Assert.DoesNotContain("chpasswd", script);
    }

    [Fact]
    public async Task Fails_WhenCockpitIsRequestedWithoutAPassword()
    {
        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        shape.Spec.Config["cockpit"] = "true";
        Environment.SetEnvironmentVariable("PODMAN_USER_PASSWORD", null);

        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));
        var result = await new PodmanProvisioner().ApplyAsync(shape, Ctx(exec));

        // Installing a login UI nobody can log into is worse than not installing it.
        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("PODMAN_USER_PASSWORD", result.Message);
    }

    // ---- assets (#303) ----------------------------------------------------

    // Adds an assets tree to a shape's stack dir and points config.assets at it.
    private Shape WithAssets(Shape s, params (string Rel, string Content)[] files)
    {
        var root = Path.Combine(s.SourceDir!, "podman-host", "assets");
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        s.Spec.Config["assets"] = "podman-host/assets";
        return s;
    }

    [Fact]
    public void Assets_AreDiscoveredRecursively_WithStablePaths()
    {
        var shape = WithAssets(PodmanShape(),
            ("config/prometheus.yml", "scrape_configs: []\n"),
            ("grafana/provisioning/datasources/datasources.yml", "apiVersion: 1\n"),
            ("grafana/dashboards/node.json", "{}\n"));

        Assert.Equal(
            new[] { "config/prometheus.yml", "grafana/dashboards/node.json", "grafana/provisioning/datasources/datasources.yml" },
            PodmanProvisioner.AssetFiles(shape));
    }

    [Fact]
    public void Assets_DefaultTarget_IsUnderTheRootlessUsersHome()
    {
        Assert.Equal("/home/podman/assets", PodmanProvisioner.AssetsTarget(PodmanShape()));
        var custom = PodmanShape();
        custom.Spec.Config["assetsTarget"] = "/home/podman/monitoring";
        Assert.Equal("/home/podman/monitoring", PodmanProvisioner.AssetsTarget(custom));
    }

    [Fact]
    public async Task Assets_ArePushedInSeparateChunkedCommands_NotInsideTheDeployScript()
    {
        var shape = WithAssets(PodmanShape(("mate.container", "[Container]\nImage=x:1\n")),
            ("config/prometheus.yml", "scrape_configs: []\n"),
            ("grafana/dashboards/node.json", "{}\n"));
        shape.Spec.Config["assetsTarget"] = "/home/podman/monitoring";

        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));
        var (msg, failed) = await PodmanProvisioner.PushAssetsAsync(shape, Ctx(exec), "pve1", "4001", "podman");

        Assert.Null(failed);
        Assert.Contains("2 asset file(s)", msg);
        // Nested parents created, content decoded, temp removed, ownership fixed.
        Assert.Contains(exec.Commands, c => c.Contains("install -d -o podman -g podman -m 755 /home/podman/monitoring/grafana/dashboards"));
        Assert.Contains(exec.Commands, c => c.Contains("base64 -d < /home/podman/monitoring/config/prometheus.yml.b64"));
        Assert.Contains(exec.Commands, c => c.Contains("rm -f /home/podman/monitoring/config/prometheus.yml.b64"));
        Assert.Contains(exec.Commands, c => c.Contains("chown -R podman:podman /home/podman/monitoring"));

        // THE POINT: no single command may approach the pct exec command-length limit.
        // CT 4001 died at 286 KiB embedded in one script; 128 KiB already fails on this path.
        Assert.All(exec.Commands, c => Assert.True(c.Length < 64 * 1024,
            $"command of {c.Length} bytes risks the pct exec command-length limit"));

        // And the deploy script itself must no longer carry them.
        Assert.DoesNotContain("prometheus.yml", Build(shape));
    }

    [Fact]
    public async Task Assets_LargeFile_IsSplitAcrossAppendingChunks()
    {
        // One file bigger than a chunk: first write truncates, the rest append, so a partial
        // previous run can never leave a truncated file behind.
        var big = new string('y', PodmanProvisioner.AssetChunkBytes * 2);
        var shape = WithAssets(PodmanShape(), ("config/big.yml", big));

        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));
        await PodmanProvisioner.PushAssetsAsync(shape, Ctx(exec), "pve1", "4001", "podman");

        var writes = exec.Commands.Where(c => c.Contains("big.yml.b64")).ToList();
        Assert.True(writes.Count >= 3, $"expected the file to be chunked, got {writes.Count} write(s)");
        Assert.Contains("> /home/podman/assets/config/big.yml.b64", writes[0]);
        Assert.DoesNotContain(">> ", writes[0]);
        Assert.Contains(">> /home/podman/assets/config/big.yml.b64", writes[1]);
    }

    [Fact]
    public async Task Assets_PushEnsuresUserAndTargetExist_BecauseItRunsBeforeTheDeployScript()
    {
        var shape = WithAssets(PodmanShape(), ("config/app.yml", "k: v\n"));
        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));

        await PodmanProvisioner.PushAssetsAsync(shape, Ctx(exec), "pve1", "4001", "podman");

        Assert.Contains(exec.Commands, c => c.Contains("useradd -m -s /bin/bash podman"));
        Assert.Contains(exec.Commands, c => c.Contains("install -d -o podman -g podman -m 755 /home/podman/assets"));
    }

    [Fact]
    public async Task Assets_ShellScriptsLandExecutable()
    {
        var shape = WithAssets(PodmanShape(), ("health.sh", "#!/bin/sh\nexit 0\n"), ("config/app.yml", "k: v\n"));
        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));

        await PodmanProvisioner.PushAssetsAsync(shape, Ctx(exec), "pve1", "4001", "podman");

        // A rendered script is the only workable healthcheck when the command needs quotes.
        Assert.Contains(exec.Commands, c => c.Contains("chmod 0755 /home/podman/assets/health.sh"));
        Assert.Contains(exec.Commands, c => c.Contains("chmod 0644 /home/podman/assets/config/app.yml"));
    }

    [Fact]
    public void Marker_ChangesWhenAnAssetChanges()
    {
        var a = WithAssets(PodmanShape(), ("config/prometheus.yml", "scrape_interval: 15s\n"));
        var b = WithAssets(PodmanShape(), ("config/prometheus.yml", "scrape_interval: 30s\n"));

        // Editing a config must re-converge (and restart the units consuming it).
        Assert.NotEqual(PodmanProvisioner.DesiredMarker(a), PodmanProvisioner.DesiredMarker(b));
    }

    [Fact]
    public void Marker_IsDeterministic_WithAssetsPresent()
    {
        var a = WithAssets(PodmanShape(), ("config/a.yml", "x\n"), ("d/b.json", "{}\n"));
        var b = WithAssets(PodmanShape(), ("config/a.yml", "x\n"), ("d/b.json", "{}\n"));

        // Regression guard: rendering per-file rather than as a tarball is what keeps this
        // stable — a tar embeds mtimes and would churn the marker on every run.
        Assert.Equal(PodmanProvisioner.DesiredMarker(a), PodmanProvisioner.DesiredMarker(b));
    }

    [Fact]
    public void Assets_TooLarge_FailLoudlyRatherThanOverflowTheCommandLine()
    {
        var shape = WithAssets(PodmanShape(), ("big.bin", new string('x', PodmanProvisioner.MaxAssetBytes + 1)));

        var ex = Assert.Throws<InvalidOperationException>(() => PodmanProvisioner.ReadAssets(shape));
        Assert.Contains("assets under", ex.Message);
    }

    [Fact]
    public void NoAssets_DeclaredMeansNothingRendered()
    {
        var script = Build(PodmanShape(("mate.container", "[Container]\nImage=x:1\n")));
        Assert.DoesNotContain("/assets", script);
    }

    [Fact]
    public void Deploy_WritesStorageConf_SoDistrolessImagesCanBeUnpacked()
    {
        var script = Build(PodmanShape(("mate.container", "[Container]\nImage=x:1\n")));

        // A distroless image owns files as uid 65532, which is outside the subuid window the
        // LXC's own 65536-uid map allows — the pull fails outright without this. Widening the
        // range can't fix it (65533 subuids inside a 65536-wide window leaves no real accounts).
        Assert.Contains("/home/podman/.config/containers/storage.conf", script);
        Assert.Contains("ignore_chown_errors", script);

        // Must precede anything that pulls an image.
        var conf = script.IndexOf("storage.conf", StringComparison.Ordinal);
        var start = script.IndexOf("systemctl --user restart", StringComparison.Ordinal);
        Assert.True(conf >= 0 && start >= 0 && conf < start,
            "storage.conf must be written before any unit (and therefore any image pull)");
    }

    // ---- host-side prerequisites ------------------------------------------

    // pct config output for a CT with the given features line.
    private static string PctConfig(string features) =>
        $"arch: amd64\ncores: 1\nfeatures: {features}\nhostname: podman-lab\nunprivileged: 1";

    [Fact]
    public async Task EnsureHostConfig_AddsTunBindMount_WhenAbsent()
    {
        var shape = PodmanShape();
        var exec = new FakeNodeExec(cmd =>
            cmd.StartsWith("pct config", StringComparison.Ordinal) ? new ExecResult(0, PctConfig("nesting=1,keyctl=1,fuse=1"), "")
            : cmd.Contains("grep -q 'dev/net'") ? new ExecResult(0, "no", "")
            : new ExecResult(0, "", ""));

        var (msg, failed) = await PodmanProvisioner.EnsureHostConfigAsync(shape, Ctx(exec), "pve1", "9900");

        Assert.Null(failed);
        Assert.Contains("/dev/net/tun", msg);
        // Rootless pasta/slirp4netns both open /dev/net/tun; bind the DIR so the mount target exists.
        Assert.Contains(exec.Commands, c => c.Contains("lxc.mount.entry: /dev/net dev/net none bind,create=dir"));
        Assert.Contains(exec.Commands, c => c.Contains("/etc/pve/lxc/9900.conf"));
        // Boot-time change → the CT must be restarted before the in-CT phase runs.
        var restart = exec.Commands.FirstOrDefault(c => c.Contains($"pct start 9900"));
        Assert.NotNull(restart);
        // Explicit stop→start, never `pct reboot`: pct status reports "running" the instant a
        // reboot is requested, so a status-based wait returns before shutdown even starts and
        // the next pct exec blocks forever (observed live on CT 9900).
        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct reboot"));
        Assert.Contains("pct stop 9900", restart);
        // Readiness is proven against systemd, not pct status, and every probe is timeout-wrapped
        // so a wedged lxc-attach can't hang converge.
        Assert.Contains("systemctl is-system-running", restart);
        Assert.Contains("timeout 5 pct exec", restart);
    }

    [Fact]
    public async Task EnsureHostConfig_ReconcilesFeatures_WhenTheCreatePathDroppedOne()
    {
        var shape = PodmanShape();
        shape.Spec.Features = new FeaturesSpec { Nesting = true, Keyctl = true, Fuse = true };

        // Live CT is missing fuse — exactly what ct/podman.sh produced on 2026-07-26.
        var exec = new FakeNodeExec(cmd =>
            cmd.StartsWith("pct config", StringComparison.Ordinal) ? new ExecResult(0, PctConfig("nesting=1,keyctl=1"), "")
            : cmd.Contains("grep -q 'dev/net'") ? new ExecResult(0, "yes", "")
            : new ExecResult(0, "", ""));

        var (msg, failed) = await PodmanProvisioner.EnsureHostConfigAsync(shape, Ctx(exec), "pve1", "9900");

        Assert.Null(failed);
        Assert.Contains("features", msg);
        Assert.Contains(exec.Commands, c => c.Contains("pct set 9900 --features") && c.Contains("fuse=1"));
    }

    [Fact]
    public async Task EnsureHostConfig_MergesFeatures_WithoutStrippingOnesWeDidNotDeclare()
    {
        var shape = PodmanShape();
        var exec = new FakeNodeExec(cmd =>
            cmd.StartsWith("pct config", StringComparison.Ordinal) ? new ExecResult(0, PctConfig("nesting=1,keyctl=1,mount=nfs"), "")
            : cmd.Contains("grep -q 'dev/net'") ? new ExecResult(0, "yes", "")
            : new ExecResult(0, "", ""));

        await PodmanProvisioner.EnsureHostConfigAsync(shape, Ctx(exec), "pve1", "9900");

        // Add-only posture: mount=nfs was not ours to remove.
        var set = exec.Commands.FirstOrDefault(c => c.Contains("--features"));
        Assert.NotNull(set);
        Assert.Contains("mount=nfs", set);
    }

    [Fact]
    public async Task EnsureHostConfig_IsNoOp_WhenFeaturesAndTunAlreadyCorrect()
    {
        var shape = PodmanShape();
        var exec = new FakeNodeExec(cmd =>
            cmd.StartsWith("pct config", StringComparison.Ordinal) ? new ExecResult(0, PctConfig("fuse=1,keyctl=1,nesting=1"), "")
            : cmd.Contains("grep -q 'dev/net'") ? new ExecResult(0, "yes", "")
            : throw new InvalidOperationException($"unexpected mutating command: {cmd}"));

        var (msg, failed) = await PodmanProvisioner.EnsureHostConfigAsync(shape, Ctx(exec), "pve1", "9900");

        Assert.Null(failed);
        Assert.Null(msg);
        // No reboot of a healthy CT — this runs on every converge.
        Assert.DoesNotContain(exec.Commands, c => c.Contains("pct reboot"));
    }

    [Fact]
    public void ParseFeatures_ReadsThePctConfigLine()
    {
        var f = PodmanProvisioner.ParseFeatures(PctConfig("nesting=1,keyctl=1,fuse=1"));
        Assert.Equal("1", f["nesting"]);
        Assert.Equal("1", f["keyctl"]);
        Assert.Equal("1", f["fuse"]);
    }

    // ---- secrets ----------------------------------------------------------

    [Fact]
    public async Task Fails_WhenADeclaredSecretIsMissingFromSecretsEnv()
    {
        // Sentinel key, per the HL<issue>_TEST_* convention already used in ConvergeCoreTests.
        // MUST NOT be a real secrets.env key: SecretsEnv.Load(null) deliberately folds in process
        // env vars, so a realistic name makes this test pass or fail depending on whether the
        // developer has sourced secrets.env. It was originally written with MATE_AUTH_PASSWORD
        // and started failing the moment that key was registered for real (#285/#299).
        const string absentKey = "HL299_TEST_ABSENT_SECRET";
        Environment.SetEnvironmentVariable(absentKey, null);   // ensure unset

        var shape = PodmanShape(("mate.container", "[Container]\nImage=x:1\n"));
        shape.Spec.Config["secrets"] = new Dictionary<string, object?> { ["mate_password"] = absentKey };

        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));
        var result = await new PodmanProvisioner().ApplyAsync(shape, Ctx(exec));

        // Better a clear failure than an empty podman secret the unit silently mis-consumes.
        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains(absentKey, result.Message);
    }

    [Fact]
    public void Deploy_CreatesSecretsAddOnly_AndNeverPassesValuesInArgv()
    {
        var shape = PodmanShape();
        var script = PodmanProvisioner.BuildDeploy(
            shape, "podman", "abc123", "/home/podman/.homelab-managed",
            Array.Empty<string>(),
            new Dictionary<string, string> { ["mate_password"] = "s3cr3t" });

        // Add-only: an existing secret is left alone (rotation is an explicit operator action).
        Assert.Contains("podman secret exists mate_password", script);
        Assert.Contains("podman secret create mate_password -", script);
        // The value goes via stdin as base64 — never argv, so it can't leak to the process table.
        Assert.DoesNotContain("s3cr3t", script);
    }

    // ---- quadlet discovery ------------------------------------------------

    [Fact]
    public void QuadletFiles_PicksUpContainerVolumeAndNetwork_ButNotKube()
    {
        var shape = PodmanShape(
            ("mate.container", "[Container]\n"),
            ("mate-data.volume", "[Volume]\n"),
            ("mate.network", "[Network]\n"),
            ("mate.kube", "[Kube]\n"));   // ADR-0009: quadlets only, no podman-kube path

        var names = PodmanProvisioner.QuadletFiles(shape).Select(Path.GetFileName).ToList();

        Assert.Contains("mate.container", names);
        Assert.Contains("mate-data.volume", names);
        Assert.Contains("mate.network", names);
        Assert.DoesNotContain("mate.kube", names);
    }

    [Fact]
    public void UnitNames_OnlyStartsContainerUnits()
    {
        var files = new[] { "/x/mate.container", "/x/mate-data.volume", "/x/mate.network" };

        // .volume/.network units are pulled in as dependencies by the containers using them.
        Assert.Equal(new[] { "mate.service" }, PodmanProvisioner.UnitNames(files));
    }

    [Fact]
    public async Task PreparesHost_EvenWithNoQuadletsDeclared()
    {
        var shape = PodmanShape();   // no quadlet dir at all
        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));

        var result = await new PodmanProvisioner().ApplyAsync(shape, Ctx(exec));

        // Phase 0 must be able to stand up a bare rootless host (the throwaway-CT acceptance
        // check) before any stack has quadlets to deploy.
        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains("no quadlets declared", result.Message);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("systemctl --user restart"));
    }

    [Fact]
    public void PlanSteps_SaysSoWhenNoQuadletsAreFound()
    {
        var steps = new PodmanProvisioner().PlanSteps(PodmanShape()).ToList();
        Assert.Contains(steps, s => s.Contains("NO quadlet files found"));
    }

    // Pull the base64 payload the script writes to `path` back out, so tests can assert on
    // real unit content instead of on the encoded blob.
    private static string DecodeUnitWrittenTo(string script, string path)
    {
        var line = script.Split('\n').FirstOrDefault(l => l.Contains($"> {path}", StringComparison.Ordinal));
        Assert.NotNull(line);
        var b64 = line!.Replace("echo ", "").Split('|')[0].Trim();
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    private static string Build(Shape s) =>
        PodmanProvisioner.BuildDeploy(
            s, PodmanProvisioner.User(s), PodmanProvisioner.DesiredMarker(s),
            $"/home/{PodmanProvisioner.User(s)}/.homelab-managed",
            PodmanProvisioner.QuadletFiles(s),
            new Dictionary<string, string>());

    // ── image extraction (#369) ─────────────────────────────────────────────────────
    // Pulls happen per image, before the deploy script, so this list drives both the progress
    // output and what gets fetched. Getting it wrong means either a missing pull or a bogus one.

    [Fact]
    public void QuadletImages_collects_each_distinct_image_once_in_order()
    {
        var shape = PodmanShape(
            ("a.container", "[Container]\nImage=ghcr.io/o/one:1.0\nContainerName=a\n"),
            ("b.container", "[Container]\nImage=docker.io/library/postgres:18.3\nContainerName=b\n"),
            // Same image as a.container — must not be pulled twice.
            ("c.container", "[Container]\nImage=ghcr.io/o/one:1.0\nContainerName=c\n"));

        var images = PodmanProvisioner.QuadletImages(PodmanProvisioner.QuadletFiles(shape));

        Assert.Equal(new[] { "ghcr.io/o/one:1.0", "docker.io/library/postgres:18.3" }, images);
    }

    [Fact]
    public void QuadletImages_ignores_files_and_values_with_nothing_to_pull()
    {
        var shape = PodmanShape(
            // A .network file has no Image= at all.
            ("x.network", "[Network]\nNetworkName=x\n"),
            // A build-target reference resolves locally; pulling it would fail.
            ("y.container", "[Container]\nImage=y.build\nContainerName=y\n"),
            ("z.container", "[Container]\nImage=ghcr.io/o/z:2\nContainerName=z\n"));

        var images = PodmanProvisioner.QuadletImages(PodmanProvisioner.QuadletFiles(shape));

        Assert.Equal(new[] { "ghcr.io/o/z:2" }, images);
    }
}
