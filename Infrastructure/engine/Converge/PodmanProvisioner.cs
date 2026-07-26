using System.Security.Cryptography;
using System.Text;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Rootless Podman + quadlet host (ADR-0009, Phase 0 / #284).
//
// CREATE half: `app: podman` → ct/podman.sh via CommunityScriptsCreator, i.e. the
// pct-over-SSH path. That matters — the Proxmox API token CANNOT set `keyctl` on an
// unprivileged LXC (see the note in Azure's ProxmoxLxcReconciler), and rootless Podman
// needs keyctl for the containers keyring exactly like Docker does. CommunityScriptsCreator
// already runs `pct create` as root over SSH and already renders
// var_unprivileged/var_nesting/var_keyctl/var_fuse from the shape's `features:`, so the
// create half needs no new code beyond declining podman-install.sh's Portainer prompts.
//
// This provisioner owns the POST-create half — turning that stock (rootful) Podman CT into
// the rootless, quadlet-native host ADR-0009 decided on:
//
//   1. Undo the rootful default. podman-install.sh runs `systemctl enable --now podman.socket`
//      — a ROOT-owned API socket, the exact thing this migration exists to remove. We disable
//      and mask it; nothing we deploy talks to a socket.
//   2. A dedicated non-root user (default `podman`) with a subuid/subgid range that FITS
//      INSIDE the LXC's own userns map (see SubidRange below — the main risk called out in
//      #284), plus `loginctl enable-linger` so its units start at boot without a login.
//   3. Quadlet files from the stack directory rendered into
//      ~<user>/.config/containers/systemd/, then `systemctl --user daemon-reload` + start.
//   4. Podman secrets seeded from secrets.env, so quadlets use `Secret=` instead of env files.
//
// Idempotent via a managed marker stamped LAST (mark-on-SUCCESS): a partial failure leaves no
// current marker, so the next converge re-runs the whole deploy.
public sealed class PodmanProvisioner : IAppProvisioner
{
    public string App => "podman";

    // The rootless user. Not root, and deliberately NOT the CT's default user either —
    // a dedicated account keeps the subuid allocation ours to manage.
    internal const string DefaultUser = "podman";

    // Subordinate-id range for the rootless user, INSIDE the container.
    //
    // This is the nested-userns trap. An unprivileged LXC is itself userns-mapped — the host
    // gives it a window (conventionally `0 100000 65536`), so inside the CT only uids 0..65535
    // exist. The host convention of handing a rootless user `100000:65536` therefore CANNOT
    // work here: it points outside the container's own map and podman fails at first run
    // ("newuidmap: write to uid_map failed"). The range must fit *within* the CT window.
    //
    // Defaults leave room for real accounts below and stay clear of 65534 (nobody):
    //   uid 1000 (the user itself) · subuid/subgid 10000..59999
    // ApplyAsync VERIFIES this against /proc/self/uid_map at apply time rather than trusting
    // the convention, and fails loudly instead of leaving a half-broken host.
    internal const int DefaultSubidStart = 10_000;
    internal const int DefaultSubidCount = 50_000;

    // Where quadlets live for a rootless user — systemd's user generator reads this path.
    internal const string QuadletDir = ".config/containers/systemd";

    // Quadlet unit extensions we render. `.kube` is deliberately absent: ADR-0009 rules out
    // any podman-kube path ("quadlets only").
    private static readonly string[] QuadletExtensions = { "*.container", "*.volume", "*.network", "*.pod" };

    public IEnumerable<string> PlanSteps(Shape s)
    {
        var user = User(s);
        var (start, count) = SubidRange(s);
        yield return $"disable+mask the ROOT podman.socket that podman-install.sh enables (rootless model owns no socket)";
        yield return $"ensure user '{user}' (+ home) with subuid/subgid {start}:{count}, verified to fit the LXC's own uid_map";
        yield return $"loginctl enable-linger {user} → user units start at boot, survive logout/reboot";

        var files = QuadletFiles(s);
        if (files.Count == 0)
            yield return $"NO quadlet files found (looked in {QuadletSourceDir(s) ?? "(unresolved stack dir)"}) — host is prepared but nothing is deployed";
        else
            yield return $"render {files.Count} quadlet(s) → ~{user}/{QuadletDir}/: {string.Join(", ", files.Select(f => Path.GetFileName(f)))}";

        var secrets = SecretNames(s);
        if (secrets.Count > 0)
            yield return $"seed podman secret(s) from secrets.env (add-only, never re-written): {string.Join(", ", secrets.Keys)}";

        if (files.Count > 0)
            yield return $"systemctl --user daemon-reload + start {string.Join(", ", UnitNames(files))} (driven over pct exec as {user})";

        if (AutoUpdate(s))
            yield return "enable podman-auto-update.timer (--user) — replaces Watchtower, no docker.sock";
    }

    // Stable marker over every managed input. Quadlet CONTENT is included (not just names),
    // so editing a .container file in the stack repo re-deploys on the next converge.
    public static string DesiredMarker(Shape s)
    {
        var (start, count) = SubidRange(s);
        var parts = new List<string>
        {
            User(s),
            start.ToString(),
            count.ToString(),
            AutoUpdate(s) ? "au=1" : "au=0",
            string.Join(",", SecretNames(s).Select(kv => $"{kv.Key}={kv.Value}")),
        };
        foreach (var f in QuadletFiles(s))
            parts.Add($"{Path.GetFileName(f)}:{Sha(SafeRead(f))}");

        return Sha(string.Join('|', parts))[..12];
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.Failed("missing node/ctid");

        var user = User(s);
        var marker = DesiredMarker(s);
        var markerPath = $"/home/{user}/.homelab-managed";

        var cur = await ctx.Exec.InContainerAsync(node, ctid, $"cat {markerPath} 2>/dev/null || true");
        if (cur.Stdout.Trim() == marker)
            return ApplyResult.NoChange($"podman host current (marker {marker})");

        // Resolve the secrets BEFORE building the script so a missing one fails the converge
        // with a clear message instead of creating an empty podman secret the units then
        // silently mis-consume.
        var secretValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (secretName, envKey) in SecretNames(s))
        {
            if (ctx.Secrets.Get(envKey) is not { Length: > 0 } val)
                return ApplyResult.Failed($"podman secret '{secretName}' needs {envKey} in secrets.env (absent/empty)");
            secretValues[secretName] = val;
        }

        var files = QuadletFiles(s);
        var script = BuildDeploy(s, user, marker, markerPath, files, secretValues);

        var res = await ctx.Exec.InContainerAsync(node, ctid, script);
        if (!res.Ok) return ApplyResult.Failed($"podman host setup failed: {res.Stderr}");

        var what = files.Count == 0
            ? "prepared rootless podman host (no quadlets declared)"
            : $"prepared rootless podman host + deployed {files.Count} quadlet(s): {string.Join(", ", UnitNames(files))}";
        return ApplyResult.Applied($"{what} (marker {marker})");
    }

    // ── the deploy script ───────────────────────────────────────────────────────────
    // One `set -e` script over pct exec. Ordering matters: the subuid guard runs BEFORE the
    // user is usable, linger before any `systemctl --user`, and the marker is stamped last.
    internal static string BuildDeploy(
        Shape s, string user, string marker, string markerPath,
        IReadOnlyList<string> files, IReadOnlyDictionary<string, string> secrets)
    {
        var (start, count) = SubidRange(s);
        var sb = new StringBuilder();
        sb.Append("set -e\n");

        // 1. Kill the rootful default. podman-install.sh enabled a root podman.socket; mask it
        //    so nothing (and no future install run) brings it back. `|| true` — a CT created
        //    before this provisioner, or a future install script that stops enabling it, has
        //    no unit to disable and that is not an error.
        sb.Append("systemctl disable --now podman.socket 2>/dev/null || true\n");
        sb.Append("systemctl mask podman.socket 2>/dev/null || true\n");

        // 2. The rootless user. `useradd -m` only when absent (idempotent).
        sb.Append($"id -u {user} >/dev/null 2>&1 || useradd -m -s /bin/bash {user}\n");
        sb.Append($"UID_{"N"}=$(id -u {user})\n");

        // 3. THE NESTED-USERNS GUARD (#284 acceptance). Read the container's own uid_map and
        //    prove the requested subid window fits inside it. /proc/self/uid_map in an
        //    unprivileged LXC reads like "         0     100000      65536" — field 3 is the
        //    size of the window we live in. If start+count exceeds it, newuidmap would fail at
        //    first container start with an opaque error; fail HERE with an explicit one instead.
        sb.Append("MAPSIZE=$(awk 'NR==1{print $3}' /proc/self/uid_map)\n");
        sb.Append("if [ -z \"$MAPSIZE\" ]; then MAPSIZE=65536; fi\n");
        sb.Append($"if [ $(({start} + {count})) -gt \"$MAPSIZE\" ]; then\n");
        sb.Append($"  echo \"subuid range {start}:{count} does not fit this LXC uid_map (size $MAPSIZE);\" >&2\n");
        sb.Append("  echo \"widen the CT's idmap on the host or lower config.subuidStart/subuidCount\" >&2\n");
        sb.Append("  exit 1\n");
        sb.Append("fi\n");

        // 4. subuid/subgid — replace any existing line for this user so a changed range
        //    converges instead of appending a second, conflicting entry.
        foreach (var f in new[] { "/etc/subuid", "/etc/subgid" })
        {
            sb.Append($"touch {f}\n");
            sb.Append($"sed -i '/^{user}:/d' {f}\n");
            sb.Append($"printf '%s\\n' '{user}:{start}:{count}' >> {f}\n");
        }
        // podman must re-read the mapping after it changes; migrate is a no-op on a fresh host.
        sb.Append($"runuser -u {user} -- podman system migrate 2>/dev/null || true\n");

        // 5. Linger, so user units start at boot with nobody logged in. This also creates
        //    /run/user/$UID, which every subsequent `systemctl --user` needs.
        sb.Append($"loginctl enable-linger {user}\n");
        // Wait for the user manager to actually be up — enable-linger returns before
        // user@.service has finished starting, and a `systemctl --user` that races it fails
        // with "Failed to connect to bus". Bounded so a genuinely broken host still errors.
        sb.Append($"for i in $(seq 1 30); do [ -S /run/user/$UID_N/bus ] && break; sleep 1; done\n");

        // 6. Quadlet files → the user's systemd generator dir. base64 so arbitrary content
        //    (quotes, $, backticks in Exec= lines) survives the shell round-trip intact.
        sb.Append($"install -d -o {user} -g {user} -m 700 /home/{user}/{QuadletDir}\n");
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(SafeRead(f)));
            sb.Append($"echo {b64} | base64 -d > /home/{user}/{QuadletDir}/{name}\n");
        }
        if (files.Count > 0)
            sb.Append($"chown -R {user}:{user} /home/{user}/{QuadletDir}\n");

        // 7. Podman secrets — ADD-ONLY (`podman secret exists || create`). Never rewritten, so
        //    a rotated value is an explicit operator action, not a silent converge side effect.
        //    Values arrive via stdin, never argv, so they can't leak into the process table.
        foreach (var (name, value) in secrets)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            sb.Append($"{UserCmd(user, $"podman secret exists {name}")} || ");
            sb.Append($"echo {b64} | base64 -d | {UserCmd(user, $"podman secret create {name} -")}\n");
        }

        // 8. Reload + start. daemon-reload runs the quadlet generator; each *.container
        //    becomes <name>.service. `restart` (not `start`) so a changed quadlet actually
        //    takes effect on an already-running unit.
        if (files.Count > 0)
        {
            sb.Append($"{UserCmd(user, "systemctl --user daemon-reload")}\n");
            foreach (var unit in UnitNames(files))
                sb.Append($"{UserCmd(user, $"systemctl --user restart {unit}")}\n");
        }

        // 9. podman auto-update replaces Watchtower (and its docker.sock mount) natively.
        if (AutoUpdate(s))
            sb.Append($"{UserCmd(user, "systemctl --user enable --now podman-auto-update.timer")}\n");

        // 10. Mark-on-SUCCESS — only reached if every step above exited 0 under `set -e`.
        sb.Append($"printf '%s' '{marker}' > {markerPath}\n");
        sb.Append($"chown {user}:{user} {markerPath}");
        return sb.ToString();
    }

    // Run a command AS the rootless user with a working user-systemd/dbus session.
    //
    // This is the "establish the working incantation" bit of #284. `machinectl shell` is the
    // other candidate but drags in systemd-container and a working dbus activation path inside
    // the CT; runuser + an explicit XDG_RUNTIME_DIR/DBUS_SESSION_BUS_ADDRESS needs neither and
    // works under `pct exec`, which has no login session, no tty and no PAM environment at all.
    // Both variables are required: systemctl --user finds the socket via XDG_RUNTIME_DIR, and
    // podman/systemd talk to the user bus via DBUS_SESSION_BUS_ADDRESS.
    internal static string UserCmd(string user, string cmd) =>
        $"runuser -u {user} -- env XDG_RUNTIME_DIR=/run/user/$UID_N " +
        $"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$UID_N/bus {cmd}";

    // ── config accessors ───────────────────────────────────────────────────────────

    internal static string User(Shape s) => s.Spec.Config.Str("user") ?? DefaultUser;

    internal static bool AutoUpdate(Shape s) =>
        s.Spec.Config.TryGetValue("autoUpdate", out var v) && v is not null
            ? v.ToString() is not ("false" or "False" or "0")
            : true;

    internal static (int Start, int Count) SubidRange(Shape s)
    {
        var c = s.Spec.Config;
        var start = int.TryParse(c.Str("subuidStart"), out var st) ? st : DefaultSubidStart;
        var count = int.TryParse(c.Str("subuidCount"), out var ct) ? ct : DefaultSubidCount;
        return (start, count);
    }

    // config.secrets — a map of podman-secret-name → secrets.env key, e.g.
    //   secrets:
    //     mate_password: MATE_AUTH_PASSWORD
    internal static Dictionary<string, string> SecretNames(Shape s)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (s.Spec.Config.TryGetValue("secrets", out var v) && v is System.Collections.IDictionary d)
            foreach (System.Collections.DictionaryEntry e in d)
                if (e.Key?.ToString() is { Length: > 0 } k && e.Value?.ToString() is { Length: > 0 } val)
                    result[k] = val;
        return result;
    }

    // Where the stack's quadlet files live on disk. Default: <stackDir>/<name>/quadlets —
    // sibling to how leapmotor-mate/ and youtarr/ already hold their compose assets.
    // Overridable with config.quadlets (relative to the stack dir, or absolute).
    internal static string? QuadletSourceDir(Shape s)
    {
        var rel = s.Spec.Config.Str("quadlets") ?? Path.Combine(s.Metadata.Name, "quadlets");
        if (Path.IsPathRooted(rel)) return rel;
        return s.SourceDir is { Length: > 0 } dir ? Path.Combine(dir, rel) : null;
    }

    internal static IReadOnlyList<string> QuadletFiles(Shape s)
    {
        var dir = QuadletSourceDir(s);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<string>();
        return QuadletExtensions
            .SelectMany(p => Directory.EnumerateFiles(dir, p))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    // <name>.container → <name>.service (podman's quadlet generator naming). .volume/.network
    // units are pulled in as dependencies by the containers that reference them, so only
    // .container files are started directly.
    internal static IReadOnlyList<string> UnitNames(IReadOnlyList<string> files) =>
        files.Where(f => f.EndsWith(".container", StringComparison.Ordinal))
             .Select(f => Path.GetFileNameWithoutExtension(f) + ".service")
             .ToList();

    private static string SafeRead(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
