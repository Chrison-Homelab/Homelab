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
        // Absent secret must never unlock the account with something weak. Originally this also
        // asserted the script contained no "passwd" at all, resting on useradd's default — which
        // was true but weaker than it looked: it only held for a host that had NEVER been given a
        // password, and said nothing about one being re-converged after the secret was withdrawn
        // (exactly what #479 does). It now asserts the lock explicitly.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p", null);

        Assert.DoesNotContain("chpasswd", script, StringComparison.Ordinal);
        Assert.Contains("passwd -l csimon", script, StringComparison.Ordinal);
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
    public void Deploy_MakesTheUsersConfigDirOwnedByTheUser()
    {
        // Regression guard. `install -d -o csimon .../.config/systemd/user` applies -o/-g to the
        // FINAL component only — every intermediate directory it creates is left owned by the
        // invoking user, i.e. root. The `chown -R` that followed started at `.config/systemd`, so
        // ~/.config itself stayed root:root and the login user could not create anything in it.
        // Observed on CT 3003 as `mkdir: cannot create directory '/home/csimon/.config/gh':
        // Permission denied` — which reads as a bug in gh rather than in the box, since ~/.config
        // is where essentially every CLI keeps its state and its login.
        var script = ShellProvisioner.BuildDeploy(ShellShape(), "m", "/p");

        // The parent is created in its own right, BEFORE the nested path that used to imply it.
        var parent = script.IndexOf("install -d -o csimon -g csimon -m 755 /home/csimon/.config\n",
                                    StringComparison.Ordinal);
        var nested = script.IndexOf("install -d -o csimon -g csimon -m 755 /home/csimon/.config/systemd/user",
                                    StringComparison.Ordinal);
        Assert.True(parent >= 0, "~/.config must be created explicitly, not as an install -d side effect");
        Assert.True(nested > parent, "the parent must be created before the nested unit directory");

        // And an already-provisioned host is repaired, non-recursively: only the directory itself
        // was ever wrong, and a -R would stamp over state dirs the tools legitimately own.
        Assert.Contains("chown csimon:csimon /home/csimon/.config\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("chown -R csimon:csimon /home/csimon/.config\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_PutsDotnetGlobalToolsOnPathUnconditionally()
    {
        // `dotnet tool install -g` drops shims in ~/.dotnet/tools and the SDK does not put that on
        // PATH, so a global tool installs successfully and is then simply not findable. It must go
        // in /etc/profile.d for the same reason brew does — Debian's .bashrc returns early for
        // non-interactive shells, which is every scripted invocation.
        var shape = ShellShape(("tmux.conf", "set -g mouse on\n"), ("Brewfile", "brew \"dotnet\"\n"));
        shape.Spec.Config["homebrew"] = true;
        var script = ShellProvisioner.BuildDeploy(shape, "m", "/p");

        Assert.Contains("/etc/profile.d/dotnet-tools.sh", script, StringComparison.Ordinal);
        Assert.Contains("$HOME/.dotnet/tools", script, StringComparison.Ordinal);
        // NOT guarded on the directory existing: it does not exist until the first global tool is
        // installed, and guarding on it makes that first install appear to have done nothing.
        Assert.DoesNotContain("[ -d \"$HOME/.dotnet/tools\" ]", script, StringComparison.Ordinal);

        // And DOTNET_ROOT, which is not optional when the SDK comes from brew: the -g shims do not
        // know brew's opt/ prefix, so every installed tool died with "You must install .NET to run
        // this application" on a box that plainly had .NET. Guarded on the path, unlike the PATH
        // entry above, because pointing DOTNET_ROOT at nothing actively breaks a system dotnet.
        Assert.Contains("export DOTNET_ROOT=\"/home/linuxbrew/.linuxbrew/opt/dotnet/libexec\"",
                        script, StringComparison.Ordinal);
        Assert.Contains("[ -d /home/linuxbrew/.linuxbrew/opt/dotnet/libexec ]", script, StringComparison.Ordinal);
    }

    // ---- Zellij web client (#479) -----------------------------------------

    private Shape ZellijShape()
    {
        var shape = ShellShape(("tmux.conf", "set -g mouse on\n"),
                               ("Brewfile", "brew \"zellij\"\n"),
                               ("zellij.kdl", "web_server_cert \"__HOME__/.config/zellij/web-cert.pem\"\n"));
        shape.Spec.Config["homebrew"] = true;
        shape.Spec.Config["zellijWeb"] = true;
        return shape;
    }

    [Fact]
    public void ZellijWebUnit_IsSimpleNotForking()
    {
        // The MIRROR IMAGE of the tmux unit, and getting it backwards costs a start timeout.
        // tmux daemonises, so it needs Type=forking. `zellij web` does NOT without `-d`: it
        // stays in the foreground, so Type=forking sits in `activating` until systemd gives up
        // while the server is actually running. Both were measured on CT 3003.
        var unit = ShellProvisioner.BuildZellijWebUnit();

        Assert.Contains("Type=simple", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Type=forking", unit, StringComparison.Ordinal);
        // No User= — user units reject it.
        Assert.DoesNotContain("User=", unit, StringComparison.Ordinal);
        // No ip/port/cert/key flags: config.kdl is the single source of truth, so a unit cannot
        // silently disagree with it.
        Assert.Contains("ExecStart=/home/linuxbrew/.linuxbrew/bin/zellij web\n", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("--ip", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("--port", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_ProvesTheZellijListenerRatherThanTrustingTheExitCode()
    {
        // `zellij web` prints "Web Server started on ..." and THEN exits if no config file
        // exists, so both the log line and the exit code lie. This host has produced three
        // reported-success-but-not-running bugs; the converge must fail here instead.
        var script = ShellProvisioner.BuildDeploy(ZellijShape(), "m", "/p");

        Assert.Contains("ss -tln | grep -q ':8082 '", script, StringComparison.Ordinal);
        Assert.Contains("NOTHING LISTENING on :8082", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_InstallsTheZellijConfigAndSubstitutesHome()
    {
        // The config file existing is load-bearing, and zellij wants ABSOLUTE cert paths — hence
        // the __HOME__ placeholder rather than a hardcoded /home/csimon in the asset.
        var script = ShellProvisioner.BuildDeploy(ZellijShape(), "m", "/p");

        Assert.Contains("/opt/homelab-shell/zellij.kdl /home/csimon/.config/zellij/config.kdl",
                        script, StringComparison.Ordinal);
        Assert.Contains("sed -i 's|__HOME__|/home/csimon|g'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_GeneratesTheCertOnlyWhenAbsent()
    {
        // Regenerating a credential on every converge would churn it for no reason and break
        // anything holding a copy. Guarded on the files being absent or empty.
        var script = ShellProvisioner.BuildDeploy(ZellijShape(), "m", "/p");

        Assert.Contains("if [ ! -s /home/csimon/.config/zellij/web-cert.pem ]", script, StringComparison.Ordinal);
        Assert.Contains("openssl req -x509", script, StringComparison.Ordinal);
        // Unattended start: the server cannot be asked for a passphrase.
        Assert.Contains("-nodes", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_WithNoPassword_MakesTheHostRefusePasswordAuth()
    {
        // An account with no password is NOT a host that refuses passwords — Debian ships
        // PasswordAuthentication yes, so sshd would still offer it for every other account.
        // With the browser terminal covering the borrowed-machine case, nothing needs it (#479).
        var script = ShellProvisioner.BuildDeploy(ZellijShape(), "m", "/p", password: null);

        Assert.Contains("PasswordAuthentication no", script, StringComparison.Ordinal);
        Assert.Contains("KbdInteractiveAuthentication no", script, StringComparison.Ordinal);
        Assert.Contains("passwd -l csimon", script, StringComparison.Ordinal);
        // sshd -t needs /run/sshd or it fails for reasons unrelated to the config, which made the
        // validate-then-reload guard protect nothing.
        Assert.Contains("mkdir -p /run/sshd", script, StringComparison.Ordinal);
        // And the reload MUST be skipped when socket-activated: SIGHUP makes sshd re-bind :22,
        // ssh.socket already holds it, and the service dies with "Cannot bind any address".
        Assert.Contains("if systemctl is-active --quiet ssh.socket; then", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_WithAPassword_LeavesSshPasswordAuthAlone()
    {
        // The hardening is the ABSENCE of the secret read as an instruction, so declaring one
        // must reverse it rather than leaving a host that refuses the password it just set.
        var script = ShellProvisioner.BuildDeploy(ZellijShape(), "m", "/p", password: "s3cret");

        Assert.DoesNotContain("PasswordAuthentication no", script, StringComparison.Ordinal);
        Assert.DoesNotContain("passwd -l csimon", script, StringComparison.Ordinal);
        Assert.Contains("chpasswd", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_WithoutTheZellijAsset_SkipsTheWebServerEntirely()
    {
        // zellijWeb with no config asset would install a unit for a server that exits on start.
        // Better to do nothing and say so in the plan than to leave a crash-looping unit.
        var shape = ShellShape(("tmux.conf", "set -g mouse on\n"));
        shape.Spec.Config["zellijWeb"] = true;
        var script = ShellProvisioner.BuildDeploy(shape, "m", "/p");

        Assert.DoesNotContain("zellij-web.service", script, StringComparison.Ordinal);
        Assert.Contains("NO zellij.kdl asset", string.Join("\n", new ShellProvisioner().PlanSteps(shape)),
                        StringComparison.Ordinal);
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
