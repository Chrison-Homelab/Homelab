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
        Assert.True(stamp > 0, "the marker must be stamped");

        // Anchored on strings that must actually be present — an IndexOf that returns -1 for a
        // renamed step would make `stamp > -1` pass for the wrong reason.
        foreach (var step in new[] { "apt-get install", "loginctl enable-linger", "systemctl --user enable tmux-main.service" })
        {
            var at = script.IndexOf(step, StringComparison.Ordinal);
            Assert.True(at > 0, $"expected the deploy script to contain '{step}'");
            Assert.True(stamp > at, $"the marker must be stamped after '{step}'");
        }
    }

    // ---- the login password (#440, browser terminal) ----------------------

    [Fact]
    public void Deploy_SetsThePasswordViaStdin_NotAnArgument()
    {
        // Pangolin's browser terminal asks for HOST credentials after the SSO gate. On a
        // borrowed machine there is no key file to upload, so a password is what makes that
        // path usable. Fed through a heredoc so the value never lands in the node's process
        // table.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p", "s3cret-value");

        Assert.Contains("chpasswd <<'HOMELAB_PW'", script, StringComparison.Ordinal);
        Assert.Contains("csimon:s3cret-value", script, StringComparison.Ordinal);
        // Never as an argument to chpasswd, and never echoed.
        Assert.DoesNotContain("chpasswd csimon", script, StringComparison.Ordinal);
        Assert.DoesNotContain("echo s3cret-value", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_LeavesTheAccountPasswordLocked_WhenNoPasswordIsSupplied()
    {
        // useradd leaves the account password-locked, i.e. key-only. Absent secret must keep it
        // that way rather than unlocking it with something weak.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p", null);

        Assert.DoesNotContain("chpasswd", script, StringComparison.Ordinal);
        Assert.DoesNotContain("passwd", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_ChangesWhenThePasswordIsAddedOrRotated()
    {
        // The silent-no-op trap, guarded. The password lives in secrets.env rather than the
        // shape, so a marker over shape inputs alone would not move when the secret is ADDED to
        // an already-provisioned host, nor when it is ROTATED — converge would report success
        // having set nothing.
        var shape = ShellShape();

        var none = ShellProvisioner.DesiredMarker(shape, null);
        var first = ShellProvisioner.DesiredMarker(shape, "pw-one");
        var rotated = ShellProvisioner.DesiredMarker(shape, "pw-two");

        Assert.NotEqual(none, first);
        Assert.NotEqual(first, rotated);
        // ...and stable for the same value.
        Assert.Equal(first, ShellProvisioner.DesiredMarker(shape, "pw-one"));
    }

    [Fact]
    public void Marker_DoesNotEmbedThePasswordItself()
    {
        // It is hashed, not carried — the marker is written to a file on the CT.
        var marker = ShellProvisioner.DesiredMarker(ShellShape(), "very-secret-value");

        Assert.DoesNotContain("very-secret-value", marker, StringComparison.Ordinal);
    }

    // ---- the toolchain (#421) ---------------------------------------------

    [Fact]
    public void Brewfile_NeverContainsTmux()
    {
        // The hazard this guards. tmux's client and server must be the SAME version, and
        // brew's bin comes FIRST on PATH — so a brew tmux would make an interactive
        // `tmux attach` run a different binary than the systemd-started server and fail with
        // "protocol version mismatch", breaking the one thing this host exists to provide.
        var brewfile = File.ReadAllText(FindRepoFile(Path.Combine("stacks", "DevOps", "shell-assets", "Brewfile")));

        Assert.DoesNotContain("brew \"tmux\"", brewfile, StringComparison.Ordinal);
        Assert.DoesNotContain("brew 'tmux'", brewfile, StringComparison.Ordinal);
        // ...and the reason is written down where someone would go to add it.
        Assert.Contains("protocol version mismatch", brewfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_InstallsHomebrewNonInteractivelyAsTheUser()
    {
        var shape = ShellShape(("Brewfile", "brew \"dotnet\"\n"));
        shape.Spec.Config["homebrew"] = true;
        var script = ShellProvisioner.BuildDeploy(shape, "m", "/p");

        // NONINTERACTIVE, or the installer's "press RETURN" prompt hangs forever under pct exec.
        Assert.Contains("NONINTERACTIVE=1", script, StringComparison.Ordinal);
        // As the login user — the installer refuses to run as root.
        Assert.Contains("runuser -l csimon", script, StringComparison.Ordinal);
        // Guarded, so a re-converge does not reinstall brew.
        Assert.Contains($"if [ ! -x {ShellProvisioner.BrewBin} ]", script, StringComparison.Ordinal);
        // Declarative: the Brewfile is what is applied.
        Assert.Contains("brew bundle --file=/opt/homelab-shell/Brewfile", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_WiresBrewOntoPathViaProfileD_NotBashrc()
    {
        // Regression guard. Debian's stock .bashrc RETURNS immediately for a non-interactive
        // shell, so an appended line never runs for `bash -lc` or `ssh host 'cmd'` — brew
        // installed fine and then reported "command not found". profile.d is read by every
        // login shell, interactive or not.
        var shape = ShellShape(("Brewfile", "brew \"dotnet\"\n"));
        shape.Spec.Config["homebrew"] = true;
        var script = ShellProvisioner.BuildDeploy(shape, "m", "/p");

        Assert.Contains("/etc/profile.d/homebrew.sh", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".bashrc", script, StringComparison.Ordinal);
        // Whole-file write, so it is idempotent without needing a grep guard.
        Assert.Contains("cat > /etc/profile.d/homebrew.sh", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_VerifiesTheClaudeSigningKeyBeforeTrustingTheRepo()
    {
        var shape = ShellShape();
        shape.Spec.Config["claudeCode"] = true;
        var script = ShellProvisioner.BuildDeploy(shape, "m", "/p");

        // Fail closed on a mismatch rather than trusting whatever the CDN served.
        Assert.Contains(ShellProvisioner.ClaudeKeyFingerprint, script, StringComparison.Ordinal);
        Assert.Contains("gpg --show-keys", script, StringComparison.Ordinal);
        Assert.Contains("refusing to trust the repo", script, StringComparison.Ordinal);
        // The fingerprint check must come BEFORE the repo is registered and apt is told to use it.
        var check = script.IndexOf("gpg --show-keys", StringComparison.Ordinal);
        var register = script.IndexOf("sources.list.d/claude-code.list", StringComparison.Ordinal);
        Assert.True(check < register, "the key must be verified before the repo is registered");
    }

    [Fact]
    public void Deploy_OmitsTheToolchain_WhenNotEnabled()
    {
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        Assert.DoesNotContain("brew", script, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-code", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_PutsTheToolchainAfterTheTerminalIsWorking()
    {
        // brew's first run can take minutes. If it fails, packages/config/session/boot unit are
        // already in place and no marker is stamped, so the next converge retries just this.
        var shape = ShellShape(("tmux.conf", "set -g mouse on\n"), ("Brewfile", "brew \"dotnet\"\n"));
        shape.Spec.Config["homebrew"] = true;
        var script = ShellProvisioner.BuildDeploy(shape, "m", "/p");

        var unit = script.IndexOf("systemctl --user enable tmux-main.service", StringComparison.Ordinal);
        var brew = script.IndexOf("NONINTERACTIVE=1", StringComparison.Ordinal);
        Assert.True(unit > 0 && brew > unit, "the toolchain must come after the boot unit");
    }

    // ---- the boot unit (#408) ---------------------------------------------

    [Fact]
    public void Unit_IsAForkingUserUnit()
    {
        // Regression guard for the bug this replaced. A SYSTEM unit with Type=oneshot +
        // RemainAfterExit=yes looks right — `tmux new -d` really does fork and exit — but
        // systemd reaps a oneshot's cgroup once ExecStart exits, killing the server it just
        // started. RemainAfterExit keeps the UNIT active, not its processes: observed on
        // CT 3003 as `active` with `Tasks: 0` and an empty cgroup.
        var unit = ShellProvisioner.BuildUnit("csimon", "main");

        Assert.Contains("Type=forking", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Type=oneshot", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("RemainAfterExit", unit, StringComparison.Ordinal);
        // A user unit must not carry User= — the user manager already runs as the user, and
        // systemd rejects the directive outright.
        Assert.DoesNotContain("User=", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/bin/tmux new-session -d -s main", unit, StringComparison.Ordinal);
        // default.target, not multi-user.target — this is the user manager's boot target.
        Assert.Contains("WantedBy=default.target", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_EnablesLingerAndInstallsTheUnitIntoTheUsersOwnSystemdDir()
    {
        // Without linger the user manager does not start at boot with nobody logged in, and
        // the unit — however correct — never runs.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        Assert.Contains("loginctl enable-linger csimon", script, StringComparison.Ordinal);
        Assert.Contains("/home/csimon/.config/systemd/user/tmux-main.service", script, StringComparison.Ordinal);
        Assert.Contains("systemctl --user enable tmux-main.service", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_RetiresTheLegacySystemUnit()
    {
        // Hosts provisioned by the first cut carry /etc/systemd/system/tmux-main.service.
        // Leaving it would race the user unit for the same session name.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        Assert.Contains("rm -f /etc/systemd/system/tmux-main.service", script, StringComparison.Ordinal);
        // ...guarded, so a host that never had one still converges.
        Assert.Contains("if [ -f /etc/systemd/system/tmux-main.service ]", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_DoesNotStartTheUnitOverAServerThatIsAlreadyRunning()
    {
        // A converge must never kill a live tmux server to take ownership of it — that is
        // someone's work. If one is up, enable the unit and let it adopt at the next boot.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        Assert.Contains("tmux has-session -t main", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_NeverKillsAServerThatIsAlreadyHoldingWork()
    {
        // TPM's install_plugins needs a live server. On a fresh host we start a scratch one and
        // kill it so the boot unit creates the real session cleanly — but on a RE-converge that
        // same kill would throw away whatever the host has been keeping alive. The kill must
        // therefore only ever appear in the no-server-running branch.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        var hasSession = script.IndexOf("tmux has-session'", StringComparison.Ordinal);
        var elseBranch = script.IndexOf("\nelse\n", hasSession, StringComparison.Ordinal);
        var kill = script.IndexOf("tmux kill-server", StringComparison.Ordinal);

        Assert.True(hasSession > 0, "the TPM step must branch on whether a server is running");
        Assert.True(kill > elseBranch && elseBranch > hasSession,
            "tmux kill-server must sit in the else (no server running) branch, never unconditionally");
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

    // Walk up from the test assembly to a repo-relative file — the test binary does not sit at
    // the repo root.
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
}
