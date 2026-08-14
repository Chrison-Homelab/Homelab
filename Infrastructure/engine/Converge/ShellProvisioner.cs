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
        "ncurses-bin", "ncurses-term", "sudo", "locales",
    };

    // TPM, and the marker file, both live under the user's home.
    private const string TpmRepo = "https://github.com/tmux-plugins/tpm";

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
        yield return $"install + enable {UnitName(session)} → tmux session '{session}' returns after a reboot (#408)";
    }

    // Stable marker over every managed input.
    public static string DesiredMarker(Shape s)
    {
        var parts = new List<string>
        {
            User(s),
            Session(s),
            string.Join(",", Packages(s)),
            string.Join(",", AuthorizedKeys(s)),
        };

        if (AssetsSourceDir(s) is { } dir)
            foreach (var rel in AssetFiles(s))
                parts.Add($"asset:{rel}:{Sha(SafeRead(Path.Combine(dir, rel)))}");

        // Hash the recipe too, not just its inputs — see PodmanProvisioner.DesiredMarker
        // for the bug this prevents (a fixed script no-opping on hosts carrying the old marker).
        parts.Add(Sha(BuildDeploy(s, "<marker>", "<markerPath>")));

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

        var marker = DesiredMarker(s);
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

        var res = await ctx.Exec.InContainerAsync(node, ctid, BuildDeploy(s, marker, markerPath));
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
    internal static string BuildDeploy(Shape s, string marker, string markerPath)
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

        // 7. TPM + plugins. install_plugins needs a live server to talk to, so start one and
        //    source the config first; `tmux kill-server` afterwards leaves the boot unit to
        //    create the real session cleanly rather than inheriting this scratch one.
        sb.Append(AsUser(user, $"[ -d {home}/.tmux/plugins/tpm ] || " +
                               $"git clone --depth 1 {TpmRepo} {home}/.tmux/plugins/tpm"));
        sb.Append(AsUser(user, "tmux start-server"));
        sb.Append(AsUser(user, $"tmux source-file {home}/.tmux.conf || true"));
        sb.Append(AsUser(user, $"{home}/.tmux/plugins/tpm/bin/install_plugins"));
        sb.Append(AsUser(user, "tmux kill-server || true"));

        // 8. The boot unit. Type=oneshot + RemainAfterExit: `tmux new -d` forks the server and
        //    exits, so there is no main process for systemd to track — oneshot models that
        //    honestly, where Type=forking would wait for a fork that already happened.
        var unit = BuildUnit(user, session);
        sb.Append($"cat > /etc/systemd/system/{UnitName(session)} <<'HOMELAB_UNIT'\n");
        sb.Append(unit);
        sb.Append("HOMELAB_UNIT\n");
        sb.Append("systemctl daemon-reload\n");
        sb.Append($"systemctl enable --now {UnitName(session)}\n");

        // 9. Marker LAST — anything above failing means no marker, means a re-run.
        sb.Append($"printf '%s' '{marker}' > {markerPath}\n");
        sb.Append($"chown {user}:{user} {markerPath}");

        return sb.ToString();
    }

    internal static string UnitName(string session) => $"tmux-{session}.service";

    internal static string BuildUnit(string user, string session) => $"""
        [Unit]
        Description=Long-lived tmux session '{session}' for {user} (homelab shell host)
        Documentation=https://github.com/Chrison-Homelab/Homelab/issues/404
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=oneshot
        RemainAfterExit=yes
        User={user}
        # tmux-resurrect restores into the session this creates; TERM has to name an entry
        # that exists on the host, not whatever the last client happened to use.
        Environment=TERM=tmux-256color
        ExecStart=/usr/bin/tmux new-session -d -s {session}
        ExecStop=/usr/bin/tmux kill-session -t {session}

        [Install]
        WantedBy=multi-user.target

        """;

    // Run a command as the login user with a real login environment. `sudo -u` alone keeps
    // root's HOME, which sends TPM's clone and the plugin install into /root.
    private static string AsUser(string user, string cmd) =>
        $"runuser -l {user} -c {Quote(cmd)}\n";

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
