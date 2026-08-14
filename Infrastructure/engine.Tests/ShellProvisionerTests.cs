using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// ShellProvisioner unit tests (#404/#405/#408). Pure + faked INodeExec — no live cluster.
// The parts worth locking down are the ones that are cheap to get wrong and expensive to
// discover: idempotency, the "nobody can log in" guard, and the two things the shell host
// exists to fix (the terminfo and the canonical tmux.conf) actually reaching the CT.
public sealed class ShellProvisionerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    // A shape whose SourceDir is a real temp stack dir, optionally holding asset files.
    private Shape ShellShape(params (string Name, string Content)[] assets)
    {
        var stackDir = Path.Combine(Path.GetTempPath(), "shell-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stackDir);
        _tempDirs.Add(stackDir);

        var s = new Shape { Metadata = new ShapeMetadata { Name = "shell", Stack = "DevOps" }, SourceDir = stackDir };
        s.Spec.Node = "desktop-01";
        s.Spec.Ctid = "3003";
        s.Spec.App = "debian";
        s.Spec.Provisioner = "shell";
        s.Spec.Config["user"] = "csimon";
        s.Spec.Config["session"] = "main";
        s.Spec.Config["authorizedKeys"] = new List<object> { "ssh-ed25519 AAAATEST csimon@mac" };

        if (assets.Length > 0)
        {
            s.Spec.Config["assets"] = "shell-assets";
            var adir = Path.Combine(stackDir, "shell-assets");
            Directory.CreateDirectory(adir);
            foreach (var (name, content) in assets)
                File.WriteAllText(Path.Combine(adir, name), content);
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

    private static FakeNodeExec OkExec(string markerReply = "") =>
        new(cmd => cmd.StartsWith("cat /home/", StringComparison.Ordinal)
            ? new ExecResult(0, markerReply, "")
            : new ExecResult(0, "", ""));

    private static ConvergeContext Ctx(INodeExec exec) =>
        new(exec, SecretsEnv.Load(null), new Dictionary<string, Shape>(), Deriver: null!);

    // ---- registry wiring --------------------------------------------------

    [Fact]
    public void Registry_DispatchesShellProvisioner_ByProvisionerName()
    {
        // The CT is created as `app: debian`; dispatch is by `provisioner: shell`.
        Assert.IsType<ShellProvisioner>(ProvisionerRegistry.Default().For("shell"));
    }

    // ---- idempotency ------------------------------------------------------

    [Fact]
    public async Task ReportsNoChange_WhenMarkerAlreadyMatches()
    {
        var shape = ShellShape(("tmux.conf", "set -g mouse on\n"));
        var marker = ShellProvisioner.DesiredMarker(shape);
        var exec = OkExec(markerReply: marker);

        var result = await new ShellProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        // Nothing beyond the marker read — no apt, no asset push.
        Assert.Single(exec.Commands);
    }

    [Fact]
    public async Task Applies_WhenMarkerIsStale()
    {
        var shape = ShellShape(("tmux.conf", "set -g mouse on\n"));
        var exec = OkExec(markerReply: "stale-marker");

        var result = await new ShellProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
    }

    [Fact]
    public void Marker_ChangesWhenAssetContentChanges()
    {
        // The whole point of hosting the config centrally is that editing it in the stack
        // repo re-converges. A marker over filenames alone would silently no-op.
        var before = ShellProvisioner.DesiredMarker(ShellShape(("tmux.conf", "set -g mouse on\n")));
        var after = ShellProvisioner.DesiredMarker(ShellShape(("tmux.conf", "set -g mouse off\n")));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Marker_IsStableForIdenticalInput()
    {
        var a = ShellProvisioner.DesiredMarker(ShellShape(("tmux.conf", "set -g mouse on\n")));
        var b = ShellProvisioner.DesiredMarker(ShellShape(("tmux.conf", "set -g mouse on\n")));

        Assert.Equal(a, b);
    }

    // ---- the lockout guard ------------------------------------------------

    [Fact]
    public async Task Fails_WhenNoAuthorizedKeysAreDeclared()
    {
        // A shell host is reached by ssh. Provisioning one nobody can log into is a failure,
        // and it must be caught BEFORE the marker is stamped or the next converge no-ops.
        var shape = ShellShape(("tmux.conf", "set -g mouse on\n"));
        shape.Spec.Config["authorizedKeys"] = new List<object>();
        var exec = OkExec();

        var result = await new ShellProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("authorizedKeys", result.Message, StringComparison.Ordinal);
        Assert.Empty(exec.Commands);
    }

    // ---- the deploy recipe ------------------------------------------------

    [Fact]
    public void Deploy_CompilesEveryTerminfoAssetIntoTheUsersOwnTree()
    {
        // The reason #405 exists: rio's entry lives in the macOS app bundle, so without a
        // compiled copy here every ssh in reports "unknown terminal type".
        var shape = ShellShape(("rio.terminfo", "rio,\n\tam,\n"));
        var script = ShellProvisioner.BuildDeploy(shape, "m", "/home/csimon/.homelab-managed");

        Assert.Contains("tic -x -o /home/csimon/.terminfo /opt/homelab-shell/rio.terminfo", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_InstallsTmuxConfAsTheUser()
    {
        var shape = ShellShape(("tmux.conf", "set -g mouse on\n"));
        var script = ShellProvisioner.BuildDeploy(shape, "m", "/home/csimon/.homelab-managed");

        Assert.Contains("install -o csimon -g csimon -m 644 /opt/homelab-shell/tmux.conf /home/csimon/.tmux.conf",
            script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_InstallsTpmPluginsNonInteractively()
    {
        // `prefix + I` has nobody to press it on an unattended host.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        Assert.Contains("tpm/bin/install_plugins", script, StringComparison.Ordinal);
        // ...and as the login user, or the clone lands in /root and the plugins never load.
        Assert.Contains("runuser -l csimon -c", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_ReplacesAuthorizedKeysRatherThanAppending()
    {
        // Appending would make key REMOVAL a manual step, leaving a revoked laptop with
        // access indefinitely. The shape is the source of truth for who can log in.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        Assert.Contains("cat > /home/csimon/.ssh/authorized_keys", script, StringComparison.Ordinal);
        Assert.DoesNotContain(">> /home/csimon/.ssh/authorized_keys", script, StringComparison.Ordinal);
        Assert.Contains("ssh-ed25519 AAAATEST csimon@mac", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_EnablesSshd()
    {
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        Assert.Contains("systemctl enable --now ssh", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_StampsTheMarkerLast()
    {
        // Mark-on-SUCCESS: a partial failure must leave no current marker so the next
        // converge re-runs the whole deploy.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "abc123", "/home/csimon/.homelab-managed");

        var stamp = script.IndexOf("printf '%s' 'abc123'", StringComparison.Ordinal);
        Assert.True(stamp > 0);
        Assert.True(stamp > script.IndexOf("apt-get install", StringComparison.Ordinal));
        Assert.True(stamp > script.IndexOf("systemctl enable --now tmux-main.service", StringComparison.Ordinal));
    }

    // ---- the boot unit (#408) ---------------------------------------------

    [Fact]
    public void Unit_IsOneshotRemainAfterExit_AndRunsAsTheUser()
    {
        // `tmux new -d` forks the server and exits, so there is no main process to track.
        // Type=forking would wait for a fork that has already happened.
        var unit = ShellProvisioner.BuildUnit("csimon", "main");

        Assert.Contains("Type=oneshot", unit, StringComparison.Ordinal);
        Assert.Contains("RemainAfterExit=yes", unit, StringComparison.Ordinal);
        Assert.Contains("User=csimon", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/bin/tmux new-session -d -s main", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=multi-user.target", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void UnitName_FollowsTheSession()
    {
        Assert.Equal("tmux-main.service", ShellProvisioner.UnitName("main"));
        Assert.Equal("tmux-work.service", ShellProvisioner.UnitName("work"));
    }

    // ---- packages ---------------------------------------------------------

    [Fact]
    public void Packages_AlwaysIncludeTheBaseline_AndDeduplicateTheShapesAdditions()
    {
        var shape = ShellShape();
        shape.Spec.Config["packages"] = new List<object> { "ripgrep", "tmux", "jq" };

        var packages = ShellProvisioner.Packages(shape);

        Assert.Contains("ripgrep", packages);
        // ncurses-term carries the tmux-256color entry `default-terminal` names; without it
        // tmux silently falls back to screen.
        Assert.Contains("ncurses-term", packages);
        Assert.Contains("ncurses-bin", packages);   // tic
        Assert.Single(packages, p => p == "tmux");
        // Ordered, so listing a package twice does not churn the marker.
        Assert.Equal(packages.OrderBy(p => p, StringComparer.Ordinal), packages);
    }

    // ---- defaults ---------------------------------------------------------

    [Fact]
    public void Config_FallsBackToDefaults_WhenUnset()
    {
        var shape = ShellShape();
        shape.Spec.Config.Remove("user");
        shape.Spec.Config.Remove("session");

        Assert.Equal(ShellProvisioner.DefaultUser, ShellProvisioner.User(shape));
        Assert.Equal(ShellProvisioner.DefaultSession, ShellProvisioner.Session(shape));
    }
}
