using System.Security.Cryptography;
using System.Text;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Shell host — a CT whose only job is to run a long-lived tmux server (#404).
//
// CREATE half: `app: debian` → ct/debian.sh, a plain base CT. There is no
// community-scripts app to install, so everything that makes this a usable
// terminal is this provisioner's work:
//
//   1. tmux + a CLI kit, and openssh-server ENABLED — the CT is reached by ssh,
//      not by pct exec, so a host without sshd running is a host you cannot use.
//   2. The login user, its authorized_keys, and passwordless sudo.
//   3. The canonical ~/.tmux.conf and the rio terminfo, rendered from the stack's
//      asset dir (#405). Both exist on the laptop only; the terminfo in particular
//      cannot be rsynced, because ~/.terminfo/72/rio is a symlink into the macOS
//      app bundle and copies across as a dangling link.
//   4. TPM plus the plugins the config declares — `prefix + I` is an interactive
//      step, and an unattended host has nobody to press it.
//   5. A systemd unit that starts the session at boot, so an LXC reboot or a
//      Proxmox maintenance window does not cost a session that has been alive for
//      months (#408). tmux-continuum's @continuum-restore does the rest.
//
// Idempotent via a managed marker stamped LAST (mark-on-SUCCESS): a partial failure
// leaves no current marker, so the next converge re-runs the whole deploy. The marker
// covers the asset CONTENT and the generated script, so editing tmux.conf in the stack
// repo — or fixing a bug in the recipe here — re-converges rather than silently
// no-opping (the trap PodmanProvisioner documents at DesiredMarker).
public sealed class ShellProvisioner : IAppProvisioner
{
    public string App => "shell";

    internal const string DefaultUser = "shell";
    internal const string DefaultSession = "main";

    // Where assets are staged inside the CT before being installed to their real homes.
    // Kept out of the user's home so a stray `rm -rf ~` does not take the staging area
    // with it, and so the tree is obviously machine-managed.
    internal const string StagingDir = "/opt/homelab-shell";

    // The baseline every shell host gets, regardless of what the shape asks for.
    //   ncurses-bin  → tic, which compiles the rio terminfo
    //   ncurses-term → the tmux-256color entry `default-terminal` names; ncurses-base
    //                  alone does NOT carry it and tmux silently falls back to screen
    //   openssh-server → the way in
    internal static readonly string[] BasePackages =
    {
        "tmux", "git", "curl", "ca-certificates", "openssh-server",
        "ncurses-bin", "ncurses-term", "sudo", "locales", "gnupg",
        // Homebrew prerequisites on Debian: it compiles from source when no bottle exists.
        "build-essential", "procps", "file",
    };

    // TPM, and the marker file, both live under the user's home.
    private const string TpmRepo = "https://github.com/tmux-plugins/tpm";

    // Optional. Present → the login account gets this password, which is what makes Pangolin's
    // browser terminal usable without a key file. Absent → the account stays password-locked.
    internal const string PasswordSecretKey = "SHELL_USER_PASSWORD";

    // ── toolchain (#421) ────────────────────────────────────────────────────────────
    internal const string BrewPrefix = "/home/linuxbrew/.linuxbrew";
    internal const string BrewBin = BrewPrefix + "/bin/brew";
    internal const string BrewfileName = "Brewfile";
    internal const string BrewInstaller = "https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh";

    // Anthropic's signed apt repo. Claude Code is a Homebrew CASK, and casks are macOS-only,
    // so brew cannot install it on Linux — this is the declarative alternative, and arguably
    // the better fit: the repo is GPG-signed and updates arrive with normal apt upgrades.
    internal const string ClaudeKeyUrl = "https://downloads.claude.ai/keys/claude-code.asc";
    internal const string ClaudeKeyPath = "/etc/apt/keyrings/claude-code.asc";
    // Verified before the repo is trusted, per Anthropic's own install docs. A key served from
    // a hijacked CDN is exactly what a fingerprint check is for.
    internal const string ClaudeKeyFingerprint = "31DDDE24DDFAB679F42D7BD2BAA929FF1A7ECACE";

    // ── Zellij web client (#479) ────────────────────────────────────────────────────
    // A browser terminal served by the host itself, behind the Pangolin/Authentik SSO gate.
    // This is what lets the OS password go away: Pangolin's `mode: ssh` resource asks for the
    // HOST's credentials after the gate, so it needed one; a web resource does not.
    internal const string ZellijConfName = "zellij.kdl";
    internal const int ZellijWebPort = 8082;
    // Under the user's own config dir because `zellij web` runs AS the user and reads both.
    // The paths are fixed rather than templated, which is why zellij.kdl can be a static asset:
    // the only other host-specific value would be the bind address, and that is 0.0.0.0.
    internal const string ZellijCertName = "web-cert.pem";
    internal const string ZellijKeyName = "web-key.pem";
    // Ten years. This certificate is never validated by anything — Traefik reaches the target
    // with `insecureSkipVerify: true` (it has to: `.internal` is not a real TLD, so no CA can
    // issue for this name and an internal CA was ruled out deliberately). It exists solely
    // because zellij REFUSES to bind a non-loopback address without one: "Cannot bind to
    // non-loopback IP: 0.0.0.0 without an SSL certificate." An expiry would therefore be a
    // scheduled outage protecting nothing.
    internal const int ZellijCertDays = 3650;
    // Written 0600 by the create step. NOT read back by converge and NOT in the marker — it is
    // generated state, like the Newt site secret, so re-running must not churn it.
    internal const string ZellijTokenFile = ".web-token";

    // Assets are small (a config and a terminfo source), but the ceiling that killed the
    // monitoring converge on CT 4001 is a property of the pct exec command line, not of
    // the payload — so chunk on the same terms rather than assuming these stay small.
    internal const int AssetChunkBytes = 32 * 1024;

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var user = User(s);
        var session = Session(s);
        var packages = Packages(s);

        yield return $"install {packages.Count} package(s): {string.Join(", ", packages)}";
        yield return $"enable sshd (the CT is reached by ssh, not pct exec)";
        yield return $"ensure user '{user}' (+ home, bash, passwordless sudo)";

        var keys = AuthorizedKeys(s);
        yield return keys.Count == 0
            ? "NO authorizedKeys declared — nobody can log in; declare config.authorizedKeys[]"
            : $"write {keys.Count} authorized key(s) → ~{user}/.ssh/authorized_keys (declarative: the file is replaced)";

        yield return $"set the login password from {PasswordSecretKey} if present, else leave the account " +
                     "password-locked (key-only)";

        var assets = AssetFiles(s);
        if (assets.Count == 0)
            yield return $"NO assets found (looked in {AssetsSourceDir(s) ?? "(unresolved stack dir)"}) — " +
                         "no tmux.conf and no terminfo will be installed";
        else
            yield return $"render {assets.Count} asset(s) → {StagingDir}/: {string.Join(", ", assets)}";

        if (assets.Contains(TmuxConfName))
            yield return $"install {TmuxConfName} → ~{user}/.tmux.conf (canonical copy — #405)";
        foreach (var ti in assets.Where(IsTerminfo))
            yield return $"tic -x {ti} → ~{user}/.terminfo (else SSH in reports 'unknown terminal type')";

        yield return $"clone TPM + run install_plugins non-interactively (no one to press prefix + I)";
        yield return $"loginctl enable-linger {user} → user units start at boot with nobody logged in";
        if (Homebrew(s))
            yield return AssetFiles(s).Contains(BrewfileName)
                ? $"install Homebrew to {BrewPrefix} as '{user}' + `brew bundle` the staged {BrewfileName}"
                : $"install Homebrew to {BrewPrefix} as '{user}' (no {BrewfileName} asset — nothing to bundle)";
        if (ClaudeCode(s))
            yield return "add Anthropic's signed apt repo (fingerprint-checked) + install claude-code " +
                         "— it is a Homebrew CASK, and casks are macOS-only, so brew cannot do it here";

        yield return $"install + enable ~{user}/.config/systemd/user/{UnitName(session)} → tmux session " +
                     $"'{session}' returns after a reboot (#408); retires the old system unit if present";

        if (ZellijWeb(s))
        {
            if (!assets.Contains(ZellijConfName))
                yield return $"zellijWeb requested but NO {ZellijConfName} asset — the web server exits " +
                             "within a second without a config file, so this is skipped";
            else
            {
                yield return $"install {ZellijConfName} → ~{user}/.config/zellij/config.kdl (MANDATORY: " +
                             "`zellij web` logs \"Failed to find default config file path\" and exits without it)";
                yield return $"generate a {ZellijCertDays}-day self-signed cert if absent → " +
                             $"~{user}/.config/zellij/{ZellijCertName} (zellij refuses a non-loopback bind without one)";
                yield return $"install + enable ~{user}/.config/systemd/user/{ZellijUnitName} (Type=simple — " +
                             "zellij web runs in the FOREGROUND despite the docs, so forking hangs in activating)";
                yield return $"VERIFY a listener on :{ZellijWebPort} — this host has produced three " +
                             "\"reported success, was not running\" bugs, so prove it rather than trust the exit code";
                yield return $"create a login token if none exists → ~{user}/.config/zellij/{ZellijTokenFile} " +
                             "(0600; shown once by zellij and unrecoverable, so it is captured, not echoed)";
            }
        }
    }

    // Stable marker over every managed input.
    //
    // `password` is a MANAGED INPUT even though it lives in secrets.env rather than the shape.
    // Its HASH is included (never the value) for two reasons: adding the secret to a host that
    // was provisioned without it must re-converge and set it, and ROTATING it must re-converge
    // too. Leaving it out is the silent-no-op trap this codebase has hit repeatedly — desired
    // state changes, the marker does not, and converge reports success having done nothing.
    public static string DesiredMarker(Shape s, string? password = null)
    {
        var parts = new List<string>
        {
            User(s),
            Session(s),
            string.Join(",", Packages(s)),
            string.Join(",", AuthorizedKeys(s)),
            password is { Length: > 0 } ? $"pw={Sha(password)[..16]}" : "pw=none",
        };

        if (AssetsSourceDir(s) is { } dir)
            foreach (var rel in AssetFiles(s))
                parts.Add($"asset:{rel}:{Sha(SafeRead(Path.Combine(dir, rel)))}");

        // Hash the recipe too, not just its inputs — see PodmanProvisioner.DesiredMarker
        // for the bug this prevents (a fixed script no-opping on hosts carrying the old marker).
        parts.Add(Sha(BuildDeploy(s, "<marker>", "<markerPath>", password is { Length: > 0 } ? "<password>" : null)));

        return Sha(string.Join('|', parts))[..12];
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.Failed("missing node/ctid");

        var user = User(s);

        // A host nobody can log into is a failed provision, not a successful one. Caught
        // here rather than after twenty minutes of apt, and before the marker is stamped.
        if (AuthorizedKeys(s).Count == 0)
            return ApplyResult.Failed(
                "no config.authorizedKeys[] declared — the shell host is reached by ssh and would " +
                "be unreachable. Declare at least one public key.");

        var password = ctx.Secrets.Get(PasswordSecretKey);
        var marker = DesiredMarker(s, password);
        var markerPath = $"/home/{user}/.homelab-managed";

        var cur = await ctx.Exec.InContainerAsync(node, ctid, $"cat {markerPath} 2>/dev/null || true");
        if (cur.Stdout.Trim() == marker)
            return ApplyResult.NoChange($"shell host current (marker {marker})");

        // Assets BEFORE the deploy script — the script installs from the staging dir, so
        // the files have to be there first. Chunked into their own commands for the same
        // reason PodmanProvisioner chunks: the limit is on the command line, not the file.
        var (assetMsg, assetFailed) = await PushAssetsAsync(s, ctx, node, ctid);
        if (assetFailed is not null) return ApplyResult.Failed(assetFailed);

        // apt on a cold CT is minutes of silence otherwise (#369).
        ctx.Report($"installing {Packages(s).Count} package(s) and configuring the tmux server for '{user}'");

        var res = await ctx.Exec.InContainerAsync(node, ctid, BuildDeploy(s, marker, markerPath, password));
        if (!res.Ok) return ApplyResult.Failed($"shell host setup failed: {res.Stderr}");

        return ApplyResult.Applied(string.Join("; ", new[]
        {
            assetMsg,
            $"prepared shell host for '{user}', session '{Session(s)}' enabled at boot (marker {marker})",
        }.Where(x => x is not null)));
    }

    // ── the deploy script ───────────────────────────────────────────────────────────
    //
    // One `pct exec` covering the whole host. `set -e` throughout, marker stamped last.
    internal static string BuildDeploy(Shape s, string marker, string markerPath, string? password = null)
    {
        var user = User(s);
        var session = Session(s);
        var home = $"/home/{user}";
        var assets = AssetFiles(s);
        var sb = new StringBuilder();

        sb.Append("set -e\n");
        sb.Append("export DEBIAN_FRONTEND=noninteractive\n");

        // 1. Packages.
        sb.Append("apt-get update -qq\n");
        sb.Append($"apt-get install -y -qq --no-install-recommends {string.Join(' ', Packages(s))}\n");

        // 2. sshd. The template ships it installed-but-not-necessarily-enabled; an ssh-only
        //    host that comes up without it is only recoverable from the node.
        sb.Append("systemctl enable --now ssh\n");

        // 3. The user. `useradd -m` only on first run; the shell and sudo membership are
        //    reasserted every time so a hand-edit drifts back.
        sb.Append($"id -u {user} >/dev/null 2>&1 || useradd -m -s /bin/bash {user}\n");
        sb.Append($"usermod -s /bin/bash -aG sudo {user}\n");
        // Passwordless sudo: the account is key-only and has no password to type.
        sb.Append($"printf '%s\\n' '{user} ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/90-{user}\n");
        sb.Append($"chmod 0440 /etc/sudoers.d/90-{user}\n");

        // 4. authorized_keys — REPLACED, not appended. The shape is the source of truth for
        //    who can log in; an append would make key removal a manual step and quietly
        //    leave a revoked laptop with access forever.
        sb.Append($"install -d -m 700 -o {user} -g {user} {home}/.ssh\n");
        sb.Append($"cat > {home}/.ssh/authorized_keys <<'HOMELAB_KEYS'\n");
        sb.Append("# MANAGED BY converge (ShellProvisioner) — edit the shape, not this file.\n");
        foreach (var k in AuthorizedKeys(s)) sb.Append(k).Append('\n');
        sb.Append("HOMELAB_KEYS\n");
        sb.Append($"chown {user}:{user} {home}/.ssh/authorized_keys\n");
        sb.Append($"chmod 600 {home}/.ssh/authorized_keys\n");

        // 4b. The login password, only when one is supplied.
        //
        //     Pangolin's SSH resource serves a browser terminal, and after the SSO gate it asks
        //     for the HOST's credentials — username+password, or an uploaded private key. On a
        //     borrowed machine there is no key file to upload, so without a password the browser
        //     path is unusable, which was the main reason for choosing that route (#406).
        //
        //     Fed through a heredoc rather than an argument so the value never appears in the
        //     node's process table. Absent → the account is left password-LOCKED (useradd's
        //     default), i.e. key-only, rather than being unlocked with something guessable.
        if (password is { Length: > 0 })
        {
            sb.Append("chpasswd <<'HOMELAB_PW'\n");
            sb.Append($"{user}:{password}\n");
            sb.Append("HOMELAB_PW\n");
        }
        else
        {
            //  NO PASSWORD DECLARED → make key-only actually true, not merely incidental.
            //
            //  An account with no password is not the same as a host that refuses passwords:
            //  Debian ships `PasswordAuthentication yes`, so sshd would still offer password
            //  auth for every other account on the box. With the browser terminal serving the
            //  no-key-on-a-borrowed-machine case (#479), nothing needs it at all.
            //
            //  A drop-in rather than an edit of sshd_config: idempotent because it is a whole
            //  file, and it survives an sshd package upgrade rewriting the main config.
            //
            //  ⚠ Locking ourselves out is not a risk here — converge reaches this CT through
            //    `pct exec` from the node, never over ssh, and the declared authorizedKeys are
            //    written earlier in this same script.
            sb.Append("install -d -m 0755 /etc/ssh/sshd_config.d\n");
            sb.Append("cat > /etc/ssh/sshd_config.d/10-homelab-keyonly.conf <<'HOMELAB_SSHD'\n");
            sb.Append("# MANAGED BY converge (ShellProvisioner) — edit the shape, not this file.\n");
            sb.Append("# Declare SHELL_USER_PASSWORD in secrets.env to go back to password auth.\n");
            sb.Append("PasswordAuthentication no\n");
            sb.Append("KbdInteractiveAuthentication no\n");
            sb.Append("HOMELAB_SSHD\n");
            sb.Append("chmod 0644 /etc/ssh/sshd_config.d/10-homelab-keyonly.conf\n");
            //  Validate before reloading: a bad sshd config that is merely written is harmless,
            //  one that is reloaded takes the daemon down.
            //  ⚠ `sshd -t` needs /run/sshd to exist or it fails with "Missing privilege
            //    separation directory" — nothing to do with the config being valid. Without this
            //    mkdir the validation always "failed", so the reload was always skipped and the
            //    guard silently protected nothing: a genuinely broken config would have looked
            //    identical. The drop-in still took effect here because Debian socket-activates
            //    sshd and each connection re-reads config, which is exactly the kind of accident
            //    that hides a broken guard.
            sb.Append("mkdir -p /run/sshd\n");
            sb.Append("sshd -t || { echo 'sshd config INVALID after writing the key-only drop-in'; exit 1; }\n");
            //  ⚠ DO NOT reload when sshd is socket-activated, which it is on Debian 13.
            //    `systemctl reload ssh` sends SIGHUP, the daemon tries to re-bind :22, and
            //    ssh.socket already holds it — "fatal: Cannot bind any address", exit 255,
            //    ssh.service failed. Observed here: SSH kept working only because the next
            //    connection socket-activated a fresh instance, which masks the fault rather
            //    than avoiding it. And no reload is NEEDED in that mode: each connection spawns
            //    an sshd that parses config fresh, so the drop-in applies immediately.
            //
            //    A plain (non-socket) sshd is the opposite — its per-connection children inherit
            //    already-parsed config, so that one does need the reload. Hence the condition
            //    rather than picking one and hoping.
            sb.Append("if systemctl is-active --quiet ssh.socket; then\n");
            sb.Append("  :  # socket-activated: new connections read the drop-in already\n");
            sb.Append("else\n");
            sb.Append("  systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || true\n");
            sb.Append("fi\n");
            //  And take the password off the account itself, so a host provisioned WITH one and
            //  re-converged without it is actually locked rather than keeping the old value.
            sb.Append($"passwd -l {user} >/dev/null 2>&1 || true\n");
        }

        // 5. tmux.conf from the staging dir.
        if (assets.Contains(TmuxConfName))
            sb.Append($"install -o {user} -g {user} -m 644 {StagingDir}/{TmuxConfName} {home}/.tmux.conf\n");

        // 6. terminfo. `tic -x -o <dir>` compiles into the user's private tree, so no
        //    system terminfo is touched and the entry survives an ncurses upgrade.
        var terminfos = assets.Where(IsTerminfo).ToList();
        if (terminfos.Count > 0)
        {
            sb.Append($"install -d -o {user} -g {user} -m 755 {home}/.terminfo\n");
            foreach (var ti in terminfos)
                sb.Append($"tic -x -o {home}/.terminfo {StagingDir}/{ti}\n");
            sb.Append($"chown -R {user}:{user} {home}/.terminfo\n");
        }

        // 7. TPM + plugins. install_plugins needs a live server to talk to.
        //
        //    The branch matters. On a fresh host we start a scratch server, install into it and
        //    kill it, so the boot unit below creates the real session cleanly instead of
        //    inheriting the scratch one. But on a RE-converge there may already be a server
        //    holding real work — killing that to install a plugin would throw away exactly what
        //    this host exists to keep. So when one is running we source the (possibly updated)
        //    config into it and install into it, and leave it alone otherwise.
        sb.Append(AsUser(user, $"[ -d {home}/.tmux/plugins/tpm ] || " +
                               $"git clone --depth 1 {TpmRepo} {home}/.tmux/plugins/tpm"));
        sb.Append($"if {AsUserInline(user, "tmux has-session")} 2>/dev/null; then\n");
        sb.Append($"  {AsUserInline(user, $"tmux source-file {home}/.tmux.conf")} || true\n");
        sb.Append($"  {AsUserInline(user, $"{home}/.tmux/plugins/tpm/bin/install_plugins")}\n");
        sb.Append("else\n");
        sb.Append($"  {AsUserInline(user, "tmux start-server")}\n");
        sb.Append($"  {AsUserInline(user, $"tmux source-file {home}/.tmux.conf")} || true\n");
        sb.Append($"  {AsUserInline(user, $"{home}/.tmux/plugins/tpm/bin/install_plugins")}\n");
        sb.Append($"  {AsUserInline(user, "tmux kill-server")} || true\n");
        sb.Append("fi\n");

        // 8. The boot unit — a USER unit under linger, not a system unit.
        //
        //    The first cut was a SYSTEM unit with Type=oneshot + RemainAfterExit=yes, reasoning
        //    that `tmux new -d` forks the server and exits so there is no main process to track.
        //    That is true, and it does not work: systemd reaps a oneshot's cgroup once ExecStart
        //    exits, so the server it just started is killed moments later. RemainAfterExit only
        //    keeps the UNIT marked active — it does not keep the processes. Observed live on
        //    CT 3003: `tmux-main.service` reported active with `Tasks: 0` and an empty cgroup,
        //    while the only surviving tmux sat in a leftover SSH session scope.
        //
        //    A user unit under `loginctl enable-linger` is the right model: the user manager
        //    starts it at boot with nobody logged in, and Type=forking tracks the daemonised
        //    server as the unit's main process, so it lives in the unit's cgroup and is not
        //    tied to any login session.
        sb.Append($"loginctl enable-linger {user}\n");
        // enable-linger returns before user@.service has finished starting, and a
        // `systemctl --user` that races it fails with "Failed to connect to bus". Bounded so a
        // genuinely broken host still errors rather than hanging. (Same wait PodmanProvisioner
        // needs, for the same reason.)
        sb.Append($"UID_N=$(id -u {user})\n");
        sb.Append("for i in $(seq 1 30); do [ -S /run/user/$UID_N/bus ] && break; sleep 1; done\n");

        //    ⚠ `install -d` applies -o/-g to the FINAL component only; every intermediate
        //    directory it has to create is left owned by the invoking user, i.e. root. So
        //    `install -d -o csimon .../.config/systemd/user` produced a root-owned ~/.config,
        //    and the `chown -R` below starts at `.config/systemd` and never repairs its parent.
        //    The result is a home directory the user cannot create anything in under ~/.config
        //    — which is where essentially every CLI keeps its state. `gh auth login` (~/.config/gh),
        //    `bw`, and zellij's own config all fail with a bare "Permission denied" that reads
        //    like a bug in the tool rather than in the box. Create the parent explicitly first.
        sb.Append($"install -d -o {user} -g {user} -m 755 {home}/.config\n");
        sb.Append($"install -d -o {user} -g {user} -m 755 {home}/.config/systemd/user\n");
        sb.Append($"cat > {home}/.config/systemd/user/{UnitName(session)} <<'HOMELAB_UNIT'\n");
        sb.Append(BuildUnit(user, session));
        sb.Append("HOMELAB_UNIT\n");
        sb.Append($"chown -R {user}:{user} {home}/.config/systemd\n");
        //    Repair an existing host provisioned before the fix above. Non-recursive on purpose:
        //    only the directory itself was ever wrong, and a -R here would stamp over whatever
        //    ownership the tools' own state directories have legitimately grown.
        sb.Append($"chown {user}:{user} {home}/.config\n");

        //    Migration: retire the system unit this provisioner used to install. Guarded so a
        //    host that never had one does not fail the converge.
        sb.Append($"if [ -f /etc/systemd/system/{UnitName(session)} ]; then\n");
        sb.Append($"  systemctl disable --now {UnitName(session)} || true\n");
        sb.Append($"  rm -f /etc/systemd/system/{UnitName(session)}\n");
        sb.Append("  systemctl daemon-reload\n");
        sb.Append("fi\n");

        sb.Append($"{UserCmd(user, "systemctl --user daemon-reload")}\n");
        sb.Append($"{UserCmd(user, $"systemctl --user enable {UnitName(session)}")}\n");
        //    Start it only if no server is already running. A converge must never kill a live
        //    tmux server to take ownership of it — that is someone's work. If one is already up
        //    (including the orphan the old system unit left behind), the unit is enabled and
        //    adopts the session at the next boot instead.
        sb.Append($"if {UserCmd(user, $"tmux has-session -t {session}")} 2>/dev/null; then\n");
        sb.Append($"  echo 'tmux server already running — unit enabled, it will own the session from the next boot'\n");
        sb.Append("else\n");
        sb.Append($"  {UserCmd(user, $"systemctl --user start {UnitName(session)}")}\n");
        sb.Append("fi\n");

        // 9. The toolchain (#421). LAST of the real work, deliberately: brew's first run can
        //    take minutes, and if it fails the terminal itself — packages, config, session,
        //    boot unit — is already in place. No marker is stamped, so the next converge
        //    retries just this.
        if (Homebrew(s))
        {
            // The installer refuses to run as root and needs sudo for /home/linuxbrew, which
            // the login user has passwordless. NONINTERACTIVE skips its "press RETURN" prompt,
            // without which this hangs forever under pct exec.
            sb.Append($"if [ ! -x {BrewBin} ]; then\n");
            sb.Append("  " + AsUserInline(user, $"NONINTERACTIVE=1 /bin/bash -c \"$(curl -fsSL {BrewInstaller})\"") + "\n");
            sb.Append("fi\n");

            // Put brew on PATH via /etc/profile.d, NOT by appending to ~/.bashrc.
            //
            // Debian's stock .bashrc opens with `case $- in *i*) ;; *) return;; esac` — it
            // RETURNS IMMEDIATELY for a non-interactive shell. An appended line therefore never
            // runs for `bash -lc`, `ssh host 'cmd'`, or anything scripted: brew appeared to
            // install fine and then `brew: command not found`, which is how this was found.
            // A profile.d drop-in is read by every login shell, interactive or not, and being a
            // whole file it is idempotent without a grep guard.
            sb.Append("cat > /etc/profile.d/homebrew.sh <<'HOMELAB_BREW_ENV'\n");
            sb.Append("# MANAGED BY converge (ShellProvisioner) — edit the shape, not this file.\n");
            sb.Append($"[ -x {BrewBin} ] && eval \"$({BrewBin} shellenv)\"\n");
            sb.Append("HOMELAB_BREW_ENV\n");
            sb.Append("chmod 0644 /etc/profile.d/homebrew.sh\n");

            // `brew bundle` is what makes this declarative rather than a pile of installs.
            if (assets.Contains(BrewfileName))
                sb.Append(AsUser(user,
                    $"eval \"$({BrewBin} shellenv)\" && brew bundle --file={StagingDir}/{BrewfileName}"));

            // ~/.dotnet/tools is where `dotnet tool install -g` puts its shims, and the SDK does
            // not put it on PATH — so a globally installed tool (proxmoxsharp, synosharp) exists
            // and is simply not findable. Same profile.d mechanism and same reason as brew above:
            // .bashrc is skipped for non-interactive shells, which is every scripted invocation.
            sb.Append("cat > /etc/profile.d/dotnet-tools.sh <<'HOMELAB_DOTNET_ENV'\n");
            sb.Append("# MANAGED BY converge (ShellProvisioner) — edit the shape, not this file.\n");
            // DOTNET_ROOT is not optional when the SDK comes from Homebrew. brew installs it under
            // its own opt/ prefix, which the `dotnet tool install -g` shims do not know about — so
            // the tool installs cleanly and then every invocation dies with "You must install .NET
            // to run this application" on a box that plainly has .NET. brew's own formula caveat
            // says to export this; nothing reads a caveat, so converge does it.
            sb.Append($"[ -d {BrewPrefix}/opt/dotnet/libexec ] && export DOTNET_ROOT=\"{BrewPrefix}/opt/dotnet/libexec\"\n");
            // NOT guarded on the directory existing. It does not exist until the first
            // `dotnet tool install -g` runs, and guarding on it makes that first install appear
            // to do nothing — the tool lands and stays unfindable until the next login. A PATH
            // entry pointing at a directory that is not there yet costs nothing.
            sb.Append("case \":$PATH:\" in *\":$HOME/.dotnet/tools:\"*) ;; *) PATH=\"$HOME/.dotnet/tools:$PATH\";; esac\n");
            sb.Append("export PATH\n");
            sb.Append("HOMELAB_DOTNET_ENV\n");
            sb.Append("chmod 0644 /etc/profile.d/dotnet-tools.sh\n");
        }

        if (ClaudeCode(s))
        {
            sb.Append("install -d -m 0755 /etc/apt/keyrings\n");
            sb.Append($"curl -fsSL {ClaudeKeyUrl} -o {ClaudeKeyPath}\n");
            // Fail closed on a fingerprint mismatch rather than trusting whatever was served.
            sb.Append($"gpg --show-keys --with-colons {ClaudeKeyPath} | grep -q '{ClaudeKeyFingerprint}' || " +
                      "{ echo 'claude-code signing key fingerprint MISMATCH — refusing to trust the repo'; exit 1; }\n");
            sb.Append($"echo 'deb [signed-by={ClaudeKeyPath}] https://downloads.claude.ai/claude-code/apt/stable stable main' " +
                      "> /etc/apt/sources.list.d/claude-code.list\n");
            sb.Append("apt-get update -qq\n");
            sb.Append("apt-get install -y -qq claude-code\n");
        }

        // 9b. Zellij web client (#479) — the browser terminal, and the reason the OS password
        //     can go away. Ordered AFTER brew, because the binary comes from the Brewfile.
        if (ZellijWeb(s) && assets.Contains(ZellijConfName))
        {
            var zdir = $"{home}/.config/zellij";
            var cert = $"{zdir}/{ZellijCertName}";
            var key = $"{zdir}/{ZellijKeyName}";
            var zellij = $"{BrewPrefix}/bin/zellij";

            sb.Append($"install -d -o {user} -g {user} -m 700 {zdir}\n");

            //  The config file is NOT optional. `zellij` itself runs on built-in defaults, but
            //  `zellij web` logs "Failed to find default config file path" and exits ~1s after
            //  printing "Web Server started on ...", so it looks like it came up and nothing
            //  listens. Installed to the canonical name the server actually looks for.
            sb.Append($"install -o {user} -g {user} -m 600 {StagingDir}/{ZellijConfName} {zdir}/config.kdl\n");
            //  zellij wants ABSOLUTE paths for the cert and key, so the asset carries a __HOME__
            //  placeholder rather than a hardcoded /home/csimon that would break if the shape's
            //  `user:` ever changed. One substitution, done here so the asset stays readable.
            sb.Append($"sed -i 's|__HOME__|{home}|g' {zdir}/config.kdl\n");

            //  Self-signed cert, generated ONLY if absent — regenerating every converge would
            //  churn a credential for no reason and invalidate any pinned copy. -nodes because
            //  the server starts unattended and cannot be asked for a passphrase.
            sb.Append($"if [ ! -s {cert} ] || [ ! -s {key} ]; then\n");
            sb.Append($"  openssl req -x509 -newkey rsa:2048 -nodes -days {ZellijCertDays} \\\n");
            sb.Append($"    -subj '/CN={ZellijCertCn}' -addext 'subjectAltName=DNS:{ZellijCertCn},DNS:localhost,IP:127.0.0.1' \\\n");
            sb.Append($"    -keyout {key} -out {cert} >/dev/null 2>&1\n");
            sb.Append($"  chown {user}:{user} {cert} {key}\n");
            //  0600 on the key: the web server runs as the user and nothing else needs it.
            sb.Append($"  chmod 600 {key}; chmod 644 {cert}\n");
            sb.Append("fi\n");

            sb.Append($"cat > {home}/.config/systemd/user/{ZellijUnitName} <<'HOMELAB_ZWEB'\n");
            sb.Append(BuildZellijWebUnit());
            sb.Append("HOMELAB_ZWEB\n");
            sb.Append($"chown {user}:{user} {home}/.config/systemd/user/{ZellijUnitName}\n");
            sb.Append(AsUser(user, $"systemctl --user daemon-reload && " +
                                   $"systemctl --user enable --now {ZellijUnitName}"));
            //  Restart rather than trust `enable --now`: on a re-converge the unit is already
            //  running with the OLD config or the OLD cert, and `--now` on an active unit is a
            //  no-op. Same bug class as the bind-mounted compose configs that never reloaded.
            sb.Append(AsUser(user, $"systemctl --user restart {ZellijUnitName}"));

            //  PROVE the listener. `zellij web` prints "Web Server started" and then dies on a
            //  missing config, so the exit code and the log line both lie. Three bugs on this
            //  host have been exactly this shape (#408 and two marker bugs), so the converge
            //  fails here rather than reporting a web terminal that is not there.
            sb.Append($"for i in $(seq 1 20); do ss -tln | grep -q ':{ZellijWebPort} ' && break; sleep 1; done\n");
            sb.Append($"ss -tln | grep -q ':{ZellijWebPort} ' || " +
                      $"{{ echo 'zellij web: NOTHING LISTENING on :{ZellijWebPort} after 20s'; " +
                      $"journalctl --user-unit {ZellijUnitName} -n 20 --no-pager 2>/dev/null || true; exit 1; }}\n");

            //  A login token is required — auth is mandatory and there is no OIDC (#479), so
            //  this is the second factor behind the Pangolin/Authentik gate. zellij displays it
            //  ONCE and hashes it, so it cannot be re-read: created only when none exists, and
            //  captured to a 0600 file for the operator to move into Bitwarden. Never echoed to
            //  the converge log, which is not a secret channel.
            sb.Append(AsUser(user, $"{zellij} web --list-tokens 2>/dev/null | grep -q . || " +
                                   $"{zellij} web --create-token > {zdir}/{ZellijTokenFile}"));
            sb.Append($"chown {user}:{user} {zdir}/{ZellijTokenFile} 2>/dev/null || true\n");
            sb.Append($"chmod 600 {zdir}/{ZellijTokenFile} 2>/dev/null || true\n");
        }

        // 10. Marker LAST — anything above failing means no marker, means a re-run.
        sb.Append($"printf '%s' '{marker}' > {markerPath}\n");
        sb.Append($"chown {user}:{user} {markerPath}");

        return sb.ToString();
    }

    internal static string UnitName(string session) => $"tmux-{session}.service";

    internal const string ZellijUnitName = "zellij-web.service";
    // The name the cert is issued for. Matches the host's UniFi local-DNS record so a human
    // hitting it directly on the LAN sees a coherent (if untrusted) certificate; Traefik does
    // not validate it either way.
    internal const string ZellijCertCn = "shell.devops.chrison.internal";

    // Type=simple, and that is the interesting part. The docs imply `zellij web` backgrounds
    // itself — `--daemonize` exists, and upstream zellij-org/zellij#4378 asks for foreground to
    // become the DEFAULT, which reads as though it is not. It already is: without `-d` the
    // process stays in the foreground, so Type=forking sits in `activating` until the start
    // timeout expires while the server is in fact up. Measured both ways on CT 3003.
    //
    // No User= — this is a user unit and systemd rejects the directive in one.
    // No ExecStart flags: ip/port/cert/key all come from config.kdl, so the CONFIG is the single
    // source of truth and a unit that disagreed with it could not silently win.
    internal static string BuildZellijWebUnit() => $"""
        [Unit]
        Description=Zellij web client (browser terminal) — homelab shell host
        Documentation=https://github.com/Chrison-Homelab/Homelab/issues/479
        After=network-online.target

        [Service]
        Type=simple
        ExecStart={BrewPrefix}/bin/zellij web
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=default.target

        """;

    // A systemd USER unit — installed to ~/.config/systemd/user, enabled under linger.
    // No User= line: the user manager already runs as the user, and setting it in a user unit
    // is an error.
    internal static string BuildUnit(string user, string session) => $"""
        [Unit]
        Description=Long-lived tmux session '{session}' for {user} (homelab shell host)
        Documentation=https://github.com/Chrison-Homelab/Homelab/issues/404

        [Service]
        # Type=forking, because `tmux new-session -d` daemonises the server and the client
        # exits. systemd then tracks the surviving server as the unit's main process, so it
        # lives in the unit's cgroup rather than in whichever login session happened to start
        # it. See the note in BuildDeploy for why the system-unit oneshot this replaces could
        # not work.
        Type=forking
        ExecStart=/usr/bin/tmux new-session -d -s {session}
        ExecStop=/usr/bin/tmux kill-session -t {session}
        Restart=on-failure
        # tmux-resurrect restores into the session this creates; TERM has to name an entry that
        # exists on the host, not whatever the last client happened to use.
        Environment=TERM=tmux-256color

        [Install]
        WantedBy=default.target

        """;

    // Run a command as the login user with a real login environment. `sudo -u` alone keeps
    // root's HOME, which sends TPM's clone and the plugin install into /root.
    private static string AsUser(string user, string cmd) => AsUserInline(user, cmd) + "\n";

    // Same, without the trailing newline — for use inside an if/else in the generated script.
    private static string AsUserInline(string user, string cmd) =>
        $"runuser -l {user} -c {Quote(cmd)}";

    // Run a command as the user with a working user-systemd/dbus session. `pct exec` has no
    // login session, no tty and no PAM environment, so `systemctl --user` cannot find the bus
    // on its own — both variables are required. Interpolates $UID_N, which BuildDeploy defines
    // before the first use. (Same incantation PodmanProvisioner established in #284.)
    internal static string UserCmd(string user, string cmd) =>
        $"runuser -u {user} -- env XDG_RUNTIME_DIR=/run/user/$UID_N " +
        $"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$UID_N/bus {cmd}";

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ── assets ──────────────────────────────────────────────────────────────────────

    internal const string TmuxConfName = "tmux.conf";

    internal static bool IsTerminfo(string rel) =>
        rel.EndsWith(".terminfo", StringComparison.Ordinal);

    internal static string? AssetsSourceDir(Shape s)
    {
        if (s.Spec.Config.Str("assets") is not { Length: > 0 } rel) return null;
        if (Path.IsPathRooted(rel)) return rel;
        return s.SourceDir is { Length: > 0 } dir ? Path.Combine(dir, rel) : null;
    }

    internal static IReadOnlyList<string> AssetFiles(Shape s)
    {
        var dir = AssetsSourceDir(s);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<(string? Msg, string? Failed)> PushAssetsAsync(
        Shape s, ConvergeContext ctx, string node, string ctid)
    {
        var dir = AssetsSourceDir(s);
        var files = AssetFiles(s);
        if (dir is null || files.Count == 0) return (null, null);

        var prep = await ctx.Exec.InContainerAsync(node, ctid, $"install -d -m 755 {StagingDir}");
        if (!prep.Ok) return (null, $"preparing {StagingDir} failed: {prep.Stderr}");

        foreach (var rel in files)
        {
            var b64 = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(dir, rel)));
            for (var off = 0; off < b64.Length; off += AssetChunkBytes)
            {
                var part = b64.Substring(off, Math.Min(AssetChunkBytes, b64.Length - off));
                var redirect = off == 0 ? ">" : ">>";
                var put = await ctx.Exec.InContainerAsync(node, ctid,
                    $"printf '%s' '{part}' {redirect} {StagingDir}/{rel}.b64");
                if (!put.Ok) return (null, $"writing {rel} (offset {off}) failed: {put.Stderr}");
            }

            var fin = await ctx.Exec.InContainerAsync(node, ctid, string.Join("\n", new[]
            {
                "set -e",
                $"base64 -d < {StagingDir}/{rel}.b64 > {StagingDir}/{rel}",
                $"rm -f {StagingDir}/{rel}.b64",
                $"chmod 0644 {StagingDir}/{rel}",
            }));
            if (!fin.Ok) return (null, $"decoding {rel} failed: {fin.Stderr}");
        }

        return ($"staged {files.Count} asset(s) → {StagingDir}", null);
    }

    // ── config accessors ────────────────────────────────────────────────────────────

    internal static bool ZellijWeb(Shape s) => Flag(s, "zellijWeb");
    internal static bool Homebrew(Shape s) => Flag(s, "homebrew");
    internal static bool ClaudeCode(Shape s) => Flag(s, "claudeCode");

    private static bool Flag(Shape s, string key) =>
        s.Spec.Config.TryGetValue(key, out var v) && v is not null
        && bool.TryParse(v.ToString(), out var b) && b;

    internal static string User(Shape s) => s.Spec.Config.Str("user") ?? DefaultUser;

    internal static string Session(Shape s) => s.Spec.Config.Str("session") ?? DefaultSession;

    internal static IReadOnlyList<string> AuthorizedKeys(Shape s) => StringList(s, "authorizedKeys");

    // Base packages first, then the shape's additions — de-duplicated, and ordered so the
    // marker does not churn when the shape lists the same package twice.
    internal static IReadOnlyList<string> Packages(Shape s) =>
        BasePackages.Concat(StringList(s, "packages"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> StringList(Shape s, string key) =>
        s.Spec.Config.TryGetValue(key, out var v) && v is IEnumerable<object> e
            ? e.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList()
            : Array.Empty<string>();

    private static string SafeRead(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
