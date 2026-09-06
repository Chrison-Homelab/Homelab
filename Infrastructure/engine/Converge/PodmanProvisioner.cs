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

        var assets = AssetFiles(s);
        if (assets.Count > 0)
            yield return $"render {assets.Count} asset file(s) → {AssetsTarget(s)}/ (config trees, dashboards, scripts)";

        var secrets = SecretNames(s);
        if (secrets.Count > 0)
            yield return $"seed podman secret(s) from secrets.env (add-only, never re-written): {string.Join(", ", secrets.Keys)}";

        if (files.Count > 0)
            yield return $"systemctl --user daemon-reload + start {string.Join(", ", UnitNames(files))} (driven over pct exec as {user})";

        if (AutoUpdate(s))
            yield return "enable podman-auto-update.timer (--user) — replaces Watchtower, no docker.sock";

        yield return "install + enable podman-system-prune.timer (weekly, images/containers/networks — never volumes)";

        if (UserSocket(s))
            yield return "enable the ROOTLESS podman.socket (--user) — opt-in, for a metrics exporter; the ROOT socket stays masked";

        if (Cockpit(s))
            yield return "install cockpit + cockpit-podman on :9090, set the `podman` user's password (PODMAN_USER_PASSWORD) so it can log in";
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
            UserSocket(s) ? "usock=1" : "usock=0",
            Cockpit(s) ? "cockpit=1" : "cockpit=0",
            string.Join(",", SecretNames(s).Select(kv => $"{kv.Key}={kv.Value}")),
        };
        foreach (var f in QuadletFiles(s))
            parts.Add($"{Path.GetFileName(f)}:{Sha(SafeRead(f))}");

        // Assets are managed inputs too: editing a rendered config file must re-converge
        // (which also restarts the units consuming it).
        if (AssetsSourceDir(s) is { } adir)
        {
            parts.Add($"assetsTarget={AssetsTarget(s)}");
            foreach (var rel in AssetFiles(s))
                parts.Add($"asset:{rel}:{Sha(SafeRead(Path.Combine(adir, rel)))}");
        }

        // Hash the generated script too, so a change to the deploy RECIPE — not just to its
        // inputs — also re-converges. Without this, fixing a bug in BuildDeploy silently
        // no-ops on every host that already carries the old marker: exactly what happened on
        // CT 9900, where the network-online fix reported NOCHANGE and never landed.
        // Placeholders for marker/path/secret-values keep it deterministic (the values
        // themselves are already covered by the secret name→key pairs above).
        parts.Add(Sha(BuildDeploy(
            s, User(s), "<marker>", "<markerPath>", QuadletFiles(s),
            SecretNames(s).ToDictionary(kv => kv.Key, _ => "", StringComparer.Ordinal))));

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
        {
            // VERIFY STILL RUNS WHEN NOTHING CHANGED, and it has to. The marker says the
            // inputs are unchanged since the last converge — it says nothing about whether
            // the consumer ever accepted them. A blueprint rejected on the run that shipped
            // it stays rejected, and every subsequent converge would report a contented
            // NOCHANGE forever. That is the same shape of blindness the whole mechanism was
            // added to remove, so skipping it here would have left the hole half-open.
            //
            // `{{since}}` is EPOCH on this path, which weakens the question from "was it
            // applied by this run" to "is it currently in the state we asked for" — the
            // strongest assertion available when, by definition, this run applied nothing.
            var (nMsg, nFailed) = await RunVerifyAsync(s, ctx, node, ctid, user, EpochSince);
            if (nFailed is not null) return ApplyResult.Failed(nFailed);
            return ApplyResult.NoChange($"podman host current (marker {marker})"
                + (nMsg is null ? "" : $"; {nMsg}"));
        }

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

        // HOST-side prerequisites first — the in-CT setup cannot fix these from inside.
        var (hostMsg, hostFailed) = await EnsureHostConfigAsync(s, ctx, node, ctid);
        if (hostFailed is not null) return ApplyResult.Failed(hostFailed);

        // Stamp the CT's clock BEFORE anything ships, so a verify step can tell "applied by
        // this converge" from "was already in that state". Read from the container rather
        // than the engine host: the comparison is against timestamps the container itself
        // writes, and the two clocks are not the same clock.
        //
        // ⚠ THE `+00` IS LOAD-BEARING AND ITS ABSENCE IS SILENT. This is UTC, but an
        // offset-less literal does not say so, and the consumer resolves it in ITS OWN
        // timezone — Postgres parses '2026-09-06 00:01:18' against the session TimeZone, and
        // the authentik container sets TZ=Pacific/Auckland. The stamp then lands ~12 hours
        // EARLIER than the instant meant, so `last_applied < '{{since}}'` is false for any
        // apply that happened today and the freshness clause never fires. The check silently
        // degrades to "is it successful right now" — the exact stale-read it exists to catch.
        // Found when a converge reported a passing verify over a blueprint it had shipped but
        // authentik had not yet applied (#539).
        var sinceRes = await ctx.Exec.InContainerAsync(node, ctid, "date -u '+%Y-%m-%d %H:%M:%S+00'");
        var since = sinceRes.Ok ? sinceRes.Stdout.Trim() : "";

        // Assets BEFORE the deploy script: units must never start before the configs they
        // mount exist. Chunked into separate commands — see AssetChunkBytes for why.
        var (assetMsg, assetFailed) = await PushAssetsAsync(s, ctx, node, ctid, user);
        if (assetFailed is not null) return ApplyResult.Failed(assetFailed);

        // Cockpit needs a PAM password for the rootless user; without it the UI installs but
        // nobody can log in, which is worse than not installing it.
        string? cockpitPassword = null;
        if (Cockpit(s))
        {
            if (ctx.Secrets.Get("PODMAN_USER_PASSWORD") is not { Length: > 0 } pw)
                return ApplyResult.Failed(
                    "cockpit: true needs PODMAN_USER_PASSWORD in secrets.env — Cockpit authenticates the " +
                    "rootless user via PAM, and it ships password-locked");
            cockpitPassword = pw;
        }

        var files = QuadletFiles(s);

        // Pull BEFORE the deploy script, one command per image.
        //
        // The deploy script is a single `pct exec` covering ten phases, and nothing is logged
        // until it returns — so a first converge with several images to fetch is 15-25 minutes of
        // total silence, indistinguishable from a wedged unit. That ambiguity got a working
        // converge cancelled at sixteen minutes (#369).
        //
        // Pulling here costs nothing (the units would pull the same layers moments later) and buys
        // two things: progress per image, and a pull failure that reports as a pull failure rather
        // than as a unit that would not start.
        var images = QuadletImages(files);
        for (var i = 0; i < images.Count; i++)
        {
            ctx.Report($"pulling image {i + 1}/{images.Count}: {images[i]}");
            var pull = await ctx.Exec.InContainerAsync(node, ctid, StandaloneUserCmd(user, $"podman pull {images[i]}"));

            // Best-effort, deliberately. On a host that does not exist yet there is no rootless user
            // and no /run/user/<uid>, so nothing can be pulled before the deploy script has run —
            // and the units below pull whatever is missing at start anyway. Failing the converge
            // here would break first-time creation to buy progress reporting on later runs.
            if (!pull.Ok)
                ctx.Report($"  pre-pull skipped ({pull.Stderr.Trim()}) — the unit will pull it at start");
        }

        if (files.Count > 0)
            ctx.Report($"images ready — running host setup and starting {files.Count} unit(s)");

        var script = BuildDeploy(s, user, marker, markerPath, files, secretValues, cockpitPassword);

        var res = await ctx.Exec.InContainerAsync(node, ctid, script);
        if (!res.Ok) return ApplyResult.Failed($"podman host setup failed: {res.Stderr}");

        // Delivery is not effect. Everything above proves files landed and units started;
        // a verify step proves the thing that consumes them actually accepted them.
        var (verifyMsg, verifyFailed) = await RunVerifyAsync(s, ctx, node, ctid, user, since);
        if (verifyFailed is not null) return ApplyResult.Failed(verifyFailed);

        var what = files.Count == 0
            ? "prepared rootless podman host (no quadlets declared)"
            : $"prepared rootless podman host + deployed {files.Count} quadlet(s): {string.Join(", ", UnitNames(files))}";
        return ApplyResult.Applied(
            string.Join("; ", new[] { hostMsg, assetMsg, $"{what} (marker {marker})", verifyMsg }.Where(x => x is not null)));
    }

    // ── verify: prove the effect, not just the delivery (#485) ──────────────────────
    //
    // WHY THIS EXISTS. A podman converge reported success while authentik had REJECTED the
    // blueprint it had just been handed: `Apply summary — 1 applied, 0 failed` and
    // `blueprint status = error` were true at the same moment. Rendering a config file and
    // starting a unit says nothing about whether the process that reads that file accepted
    // it, and for anything applied asynchronously — blueprints, dashboard provisioning,
    // scrape configs — the answer arrives seconds after converge has already exited.
    //
    // A verify step is a command run in the CT after the units are up. EMPTY STDOUT MEANS
    // GOOD; any output is the reason it is not. That inversion is deliberate: the natural way
    // to write these is a query for what is WRONG, and a query for what is wrong returns
    // nothing when nothing is.
    //
    // `{{since}}` is substituted with the CT's clock from before anything shipped, which is
    // what lets a check distinguish "applied by this run" from "was already fine". Without it
    // a status left over from an earlier converge reads as a pass, which is the same
    // stale-read failure the mechanism is meant to catch.
    // Substituted for {{since}} when a run changed nothing, so the freshness clause becomes
    // vacuously true and the check reduces to "is it successful right now".
    //
    // Carries the same explicit `+00` as a real stamp. Nothing depends on it at the epoch —
    // every plausible timestamp is after 1970 in any timezone — but the two must stay the
    // same shape, or the next person copies the offset-less one back into the live path.
    internal const string EpochSince = "1970-01-01 00:00:00+00";

    internal readonly record struct VerifyStep(string Name, string Run, int Retries, int IntervalSeconds);

    internal static IReadOnlyList<VerifyStep> VerifySteps(Shape s)
    {
        var list = new List<VerifyStep>();
        if (!(s.Spec.Config.TryGetValue("verify", out var v) && v is IEnumerable<object> items)) return list;
        foreach (var it in items)
        {
            if (it is not System.Collections.IDictionary d) continue;
            string? Str(string k) => d[k]?.ToString() is { Length: > 0 } x ? x : null;
            static int Int(System.Collections.IDictionary dd, string k, int dflt) =>
                dd[k] is { } raw && int.TryParse(raw.ToString(), out var n) && n > 0 ? n : dflt;
            if (Str("run") is not { } run) continue;
            list.Add(new VerifyStep(Str("name") ?? "verify", run, Int(d, "retries", 12), Int(d, "intervalSeconds", 10)));
        }
        return list;
    }

    // Substitution is done here rather than in the shape so the token cannot be forgotten in
    // a way that silently degrades the check into a stale-read.
    internal static string RenderVerify(string run, string since) => run.Replace("{{since}}", since);

    private static async Task<(string? Msg, string? Failed)> RunVerifyAsync(
        Shape s, ConvergeContext ctx, string node, string ctid, string user, string since)
    {
        var steps = VerifySteps(s);
        if (steps.Count == 0) return (null, null);

        foreach (var step in steps)
        {
            var cmd = StandaloneUserCmd(user, RenderVerify(step.Run, since));
            string last = "";
            var ok = false;
            for (var attempt = 1; attempt <= step.Retries; attempt++)
            {
                var r = await ctx.Exec.InContainerAsync(node, ctid, cmd);
                last = (r.Stdout + r.Stderr).Trim();
                // A command that cannot run at all is not a pass. Treated the same as output:
                // it becomes the reason, rather than being swallowed as "nothing to report".
                if (r.Ok && last.Length == 0) { ok = true; break; }
                if (attempt < step.Retries)
                {
                    ctx.Report($"verify '{step.Name}': not satisfied yet (attempt {attempt}/{step.Retries})");
                    await Task.Delay(TimeSpan.FromSeconds(step.IntervalSeconds));
                }
            }
            if (!ok)
                return (null, $"verify '{step.Name}' failed after {step.Retries} attempt(s): {Truncate(last)}");
            ctx.Report($"verify '{step.Name}': ok");
        }
        return ($"{steps.Count} verify step(s) passed", null);
    }

    private static string Truncate(string s) =>
        s.Length <= 400 ? (s.Length == 0 ? "(no output, command failed)" : s) : s[..400] + "…";

    // ── assets: chunked push (#303) ─────────────────────────────────────────────────
    // One command per chunk so no single `pct exec` command line can overflow. Ensures the
    // user and target directory exist first, because this runs BEFORE the deploy script.
    // Idempotent: every file is rewritten from its first chunk, so a partial previous run
    // cannot leave a truncated file behind.
    internal static async Task<(string? Msg, string? Failed)> PushAssetsAsync(
        Shape s, ConvergeContext ctx, string node, string ctid, string user)
    {
        IReadOnlyList<(string Rel, string B64, bool Exec)> assets;
        try { assets = ReadAssets(s); }
        catch (Exception ex) { return (null, ex.Message); }
        if (assets.Count == 0) return (null, null);
        return await PushFilesAsync(ctx.Exec, node, ctid, user, AssetsTarget(s), assets);
    }

    // The chunked push itself, for any list of files → <target>/<rel>. Shared with the
    // dashboard deploy (ADR-0012), which delivers ONE rendered file outside a converge.
    internal static async Task<(string? Msg, string? Failed)> PushFilesAsync(
        INodeExec exec, string node, string ctid, string user, string target,
        IReadOnlyList<(string Rel, string B64, bool Exec)> assets)
    {
        // The deploy script also does this (idempotently) — but assets land first, so the
        // user and directory have to exist by now.
        var prep = await exec.InContainerAsync(node, ctid, string.Join("\n", new[]
        {
            "set -e",
            $"id -u {user} >/dev/null 2>&1 || useradd -m -s /bin/bash {user}",
            $"install -d -o {user} -g {user} -m 755 {target}",
        }));
        if (!prep.Ok) return (null, $"preparing assets dir {target} failed: {prep.Stderr}");

        var chunks = 0;
        foreach (var (rel, b64, isExec) in assets)
        {
            var dirPart = Path.GetDirectoryName(rel)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dirPart))
            {
                var mk = await exec.InContainerAsync(node, ctid,
                    $"install -d -o {user} -g {user} -m 755 {target}/{dirPart}");
                if (!mk.Ok) return (null, $"creating {target}/{dirPart} failed: {mk.Stderr}");
            }

            // First chunk truncates, the rest append; then decode once. Writing base64 and
            // decoding at the end keeps each command self-contained and restartable.
            for (var off = 0; off < b64.Length; off += AssetChunkBytes)
            {
                var part = b64.Substring(off, Math.Min(AssetChunkBytes, b64.Length - off));
                var redirect = off == 0 ? ">" : ">>";
                var put = await exec.InContainerAsync(node, ctid,
                    $"printf '%s' '{part}' {redirect} {target}/{rel}.b64");
                if (!put.Ok) return (null, $"writing {rel} (offset {off}) failed: {put.Stderr}");
                chunks++;
            }

            var fin = await exec.InContainerAsync(node, ctid, string.Join("\n", new[]
            {
                "set -e",
                $"base64 -d < {target}/{rel}.b64 > {target}/{rel}",
                $"rm -f {target}/{rel}.b64",
                $"chmod {(isExec ? "0755" : "0644")} {target}/{rel}",
            }));
            if (!fin.Ok) return (null, $"decoding {rel} failed: {fin.Stderr}");
        }

        var own = await exec.InContainerAsync(node, ctid, $"chown -R {user}:{user} {target}");
        if (!own.Ok) return (null, $"chown {target} failed: {own.Stderr}");

        return ($"rendered {assets.Count} asset file(s) → {target} ({chunks} chunk(s))", null);
    }

    // ── host-side prerequisites (pct/SSH, not in-CT) ────────────────────────────────
    // Two things rootless Podman needs that only the HOST can grant, both found the hard
    // way on the first live provision (2026-07-26, throwaway CT 9900):
    //
    //  1. /dev/net/tun. Rootless networking is pasta (or slirp4netns), and BOTH open
    //     /dev/net/tun to build the tap device. An LXC has no /dev/net, so every container
    //     start dies with "pasta failed ... Failed to open() /dev/net/tun". ADR-0009 noted
    //     rootless networking has no routable IP but not this prerequisite. The host device
    //     node is mode 666, so an unprivileged CT can open it once bind-mounted — no
    //     privileged container, no host-net workaround needed.
    //  2. The declared `features:` actually landing. ct/podman.sh accepted var_fuse and
    //     created the CT with only `nesting=1,keyctl=1` — fuse was silently dropped. Since
    //     the shape is the source of truth, reconcile features here rather than trusting the
    //     create path. Merges with (never strips) features we didn't declare.
    //
    // Both need a CT restart to take effect, so they're applied together, then rebooted once.
    // Returns (message, failedReason).
    internal static async Task<(string? Msg, string? Failed)> EnsureHostConfigAsync(
        Shape s, ConvergeContext ctx, string node, string ctid)
    {
        var changed = new List<string>();

        // 1. features — merge desired over live, only writing when something is missing.
        var cfg = await ctx.Exec.OnNodeAsync(node, $"pct config {ctid}");
        if (!cfg.Ok) return (null, $"pct config {ctid} failed: {cfg.Stderr}");

        var live = ParseFeatures(cfg.Stdout);
        var desired = new Dictionary<string, string>(live, StringComparer.Ordinal);
        // Rootless podman needs all three: nesting for its own userns, keyctl for the
        // containers keyring, fuse for the fuse-overlayfs fallback storage driver.
        if (s.Spec.Features?.Nesting ?? true) desired["nesting"] = "1";
        if (s.Spec.Features?.Keyctl ?? true) desired["keyctl"] = "1";
        if (s.Spec.Features?.Fuse ?? true) desired["fuse"] = "1";

        if (desired.Count != live.Count || desired.Any(kv => live.GetValueOrDefault(kv.Key) != kv.Value))
        {
            var joined = string.Join(",", desired.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
            var set = await ctx.Exec.OnNodeAsync(node, $"pct set {ctid} --features {joined}");
            if (!set.Ok) return (null, $"pct set --features failed: {set.Stderr}");
            changed.Add($"features →{joined}");
        }

        // 2. /dev/net/tun bind-mount. Written straight to the CT conf — `pct set` has no
        //    option for raw lxc.* keys. Bind the /dev/net DIRECTORY (create=dir) rather than
        //    the tun file: binding the file alone fails when the container has no /dev/net
        //    parent to mount onto. Idempotent by grep; append-only, so nothing else in the
        //    conf is touched.
        var conf = $"/etc/pve/lxc/{ctid}.conf";
        var has = await ctx.Exec.OnNodeAsync(node, $"grep -q 'dev/net' {conf} && echo yes || echo no");
        if (has.Stdout.Trim() != "yes")
        {
            var append =
                $"printf '%s\\n%s\\n' 'lxc.cgroup2.devices.allow: c 10:200 rwm' " +
                $"'lxc.mount.entry: /dev/net dev/net none bind,create=dir' >> {conf}";
            var add = await ctx.Exec.OnNodeAsync(node, append);
            if (!add.Ok) return (null, $"adding /dev/net bind-mount to {conf} failed: {add.Stderr}");
            changed.Add("/dev/net/tun bind-mount (rootless pasta networking)");
        }

        if (changed.Count == 0) return (null, null);

        // Both changes are boot-time, so the CT has to be restarted before the in-CT phase.
        //
        // Explicit stop→start, NOT `pct reboot`, and the readiness probe checks systemd rather
        // than `pct status`. Learned the hard way on CT 9900: `pct status` reports "running"
        // the instant a reboot is requested — before shutdown has even begun — so a
        // status-based wait returns immediately, the next `pct exec` attaches to a container
        // that is mid-shutdown, and it blocks FOREVER (which in turn wedges the reboot, so the
        // CT never actually restarts). Stop-then-start gives an unambiguous "stopped" state to
        // wait on, and `systemctl is-system-running` proves the init system is actually up.
        // Every probe is `timeout`-wrapped so a wedged lxc-attach can never hang converge.
        var restart = await ctx.Exec.OnNodeAsync(node, string.Join("\n", new[]
        {
            $"pct stop {ctid} || true",
            $"for i in $(seq 1 60); do pct status {ctid} | grep -q stopped && break; sleep 2; done",
            $"pct status {ctid} | grep -q stopped || {{ echo 'CT {ctid} would not stop' >&2; exit 1; }}",
            $"pct start {ctid}",
            // running|degraded both mean init finished; a unit failing elsewhere is not our gate.
            $"for i in $(seq 1 90); do",
            $"  s=$(timeout 5 pct exec {ctid} -- systemctl is-system-running 2>/dev/null || true)",
            $"  case \"$s\" in running|degraded) exit 0;; esac",
            "  sleep 2",
            "done",
            $"echo 'CT {ctid} systemd did not come up after restart' >&2; exit 1",
        }));
        if (!restart.Ok) return (null, $"restarting CT {ctid} failed: {restart.Stderr}");

        return ($"host config: {string.Join(", ", changed)} (CT restarted)", null);
    }

    // `pct config` prints features as one line: "features: nesting=1,keyctl=1".
    internal static Dictionary<string, string> ParseFeatures(string pctConfig)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in pctConfig.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("features:", StringComparison.Ordinal)) continue;
            foreach (var pair in line["features:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Trim().Split('=', 2);
                if (kv.Length == 2 && kv[0].Length > 0) map[kv[0]] = kv[1];
            }
        }
        return map;
    }

    // ── the deploy script ───────────────────────────────────────────────────────────
    // One `set -e` script over pct exec. Ordering matters: the subuid guard runs BEFORE the
    // user is usable, linger before any `systemctl --user`, and the marker is stamped last.
    internal static string BuildDeploy(
        Shape s, string user, string marker, string markerPath,
        IReadOnlyList<string> files, IReadOnlyDictionary<string, string> secrets,
        string? cockpitPassword = null)
    {
        var (start, count) = SubidRange(s);
        var sb = new StringBuilder();
        sb.Append("set -e\n");
        // cwd matters: `pct exec` lands in /root, which the rootless user cannot read, and
        // `runuser` keeps the caller's cwd. Every podman call would then die with
        // "cannot chdir to /root: Permission denied" (found live on CT 9900 — it made
        // `podman secret create` fail while `systemctl --user` appeared fine, since systemd
        // sets its own working directory). Move somewhere world-readable first.
        sb.Append("cd /\n");

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

        // 4a. storage.conf: ignore_chown_errors.
        //
        //     Rootless podman must map every uid an image LAYER contains, and our subuid
        //     window is bounded by the LXC's own 65536-uid map — so a DISTROLESS image whose
        //     files are owned by 65532 (`/home/nonroot`, the distroless convention) cannot be
        //     unpacked at all:
        //       "potentially insufficient UIDs or GIDs available in user namespace
        //        (requested 65532:65532 for /home/nonroot)"
        //     Widening the range is not an option: mapping container uid 65532 would need
        //     ~65533 subuids inside a 65536-wide window, leaving nothing for real accounts.
        //
        //     ignore_chown_errors makes podman map those files to the user instead of failing.
        //     Hit on CT 4001 with ghcr.io/onedr0p/exportarr (#303). Note this only fixes the
        //     PULL — running such an image as its own high uid additionally needs
        //     `UserNS=keep-id:uid=<uid>,gid=<uid>` on the quadlet, or crun rejects it with
        //     "setgroups: Invalid argument".
        sb.Append($"install -d -o {user} -g {user} -m 755 /home/{user}/.config/containers\n");
        sb.Append($"printf '%s\\n' '[storage]' 'driver = \"overlay\"' '[storage.options.overlay]' " +
                  $"'ignore_chown_errors = \"true\"' > /home/{user}/.config/containers/storage.conf\n");
        sb.Append($"chown -R {user}:{user} /home/{user}/.config/containers\n");

        // 4b. Make network-online.target actually REACHABLE — without this, quadlets don't
        //     start at boot until a 90-second timeout expires.
        //
        //     Podman injects `Wants=/After=podman-user-wait-network-online.service` into every
        //     generated container unit (containers/podman#22197). That helper is literally
        //     `until systemctl is-active network-online.target; do sleep 0.5; done`. A stock
        //     community-scripts Debian LXC uses ifupdown (`networking.service`) and ships only
        //     systemd-networkd wait-online units, which aren't in play — so
        //     network-online.target is NEVER reached, the helper spins until it times out and
        //     fails, and only then does the container start. Observed live on CT 9900: the CT
        //     booted at 18:12:04 and hello.service came up at 18:13:36 — a 92s delay, every
        //     boot. It "survives a reboot" but only by waiting out a failure.
        //
        //     Fix the cause: a tiny oneshot ordered after ifupdown has finished bringing
        //     interfaces up (ifup blocks on DHCP, so its completion is a real readiness
        //     signal), which pulls network-online.target in and lets it activate. Masking the
        //     podman helper would also remove the delay but would throw away the
        //     network-readiness guarantee the quadlets legitimately want.
        var netUnit = string.Join("\n", new[]
        {
            "[Unit]",
            "Description=Reach network-online.target under ifupdown (LXC; no wait-online unit)",
            "Documentation=https://github.com/containers/podman/issues/22197",
            "After=networking.service",
            "Wants=network-online.target",
            "Before=network-online.target",
            "",
            "[Service]",
            "Type=oneshot",
            "ExecStart=/bin/true",
            "RemainAfterExit=yes",
            "",
            "[Install]",
            "WantedBy=multi-user.target",
            "",
        });
        sb.Append($"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(netUnit))} | base64 -d " +
                  "> /etc/systemd/system/homelab-network-online.service\n");
        sb.Append("systemctl daemon-reload\n");
        sb.Append("systemctl enable --now homelab-network-online.service\n");

        // 5. Linger, so user units start at boot with nobody logged in. This also creates
        //    /run/user/$UID, which every subsequent `systemctl --user` needs.
        sb.Append($"loginctl enable-linger {user}\n");
        // Wait for the user manager to actually be up — enable-linger returns before
        // user@.service has finished starting, and a `systemctl --user` that races it fails
        // with "Failed to connect to bus". Bounded so a genuinely broken host still errors.
        sb.Append($"for i in $(seq 1 30); do [ -S /run/user/$UID_N/bus ] && break; sleep 1; done\n");

        // NOTE: assets are NOT rendered here — they go out as separate chunked commands via
        //       PushAssetsAsync before this script runs, because embedding them blew past the
        //       pct exec command-length limit (see AssetChunkBytes).

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

        // 7b. Weekly prune timer — BEFORE starting app units, deliberately. Platform
        //     housekeeping must not be hostage to an application unit: when podman-exporter
        //     failed to start on CT 4001/5114 (#283 phase 3), `set -e` aborted the script and
        //     the timer + socket steps never ran at all.
        //     Weekly prune timer — the other half of "podman-native replaces the Watchtower +
        //     prune sidecars" (ADR-0009). Written as USER units so it prunes the rootless
        //     store, not root's.
        //
        //     Deliberately NOT `--volumes`: an unattached volume may still hold real data (a
        //     stopped service's database), and losing it to a housekeeping timer would be
        //     unrecoverable. Images/containers/networks only, and only things older than a week.
        sb.Append($"install -d -o {user} -g {user} -m 755 /home/{user}/.config/systemd/user\n");
        sb.Append($"printf '%s\\n' '[Unit]' 'Description=Prune unused podman images, containers and networks' " +
                  "'Documentation=ADR-0009 phase 3' '' '[Service]' 'Type=oneshot' " +
                  "'ExecStart=/usr/bin/podman system prune -af --filter until=168h' " +
                  $"> /home/{user}/.config/systemd/user/podman-system-prune.service\n");
        sb.Append($"printf '%s\\n' '[Unit]' 'Description=Weekly podman prune' '' '[Timer]' " +
                  "'OnCalendar=weekly' 'Persistent=true' 'RandomizedDelaySec=1h' '' '[Install]' " +
                  $"'WantedBy=timers.target' > /home/{user}/.config/systemd/user/podman-system-prune.timer\n");
        sb.Append($"chown -R {user}:{user} /home/{user}/.config/systemd\n");
        sb.Append($"{UserCmd(user, "systemctl --user daemon-reload")}\n");
        sb.Append($"{UserCmd(user, "systemctl --user enable --now podman-system-prune.timer")}\n");

        // 7c. The ROOTLESS user API socket — opt-in via `config.userSocket: true`.
        //
        //     ADR-0009 masks the ROOT podman.socket, and that stays masked. This is a
        //     categorically smaller thing: a socket owned by the unprivileged `podman` user,
        //     inside its own userns, reachable only by that user. It exists because a metrics
        //     exporter has no other way to enumerate containers (ADR-0009 phase 3, "observe").
        //     Off by default, so a host that doesn't export metrics still has no API surface.
        if (UserSocket(s))
            sb.Append($"{UserCmd(user, "systemctl --user enable --now podman.socket")}\n");

        // 7d. Cockpit — the management UI half of ADR-0009 phase 3.
        //
        //     Deliberately logs in as the `podman` USER, not root: rootless containers exist
        //     only inside that user's session, so a root Cockpit session shows an empty
        //     container list. That means the user needs a real PAM password — both `podman`
        //     and `root` ship password-LOCKED on a community-scripts CT, so Cockpit login is
        //     impossible until one is set. cockpit-podman then reads the same rootless socket
        //     the exporter uses, which is why this requires userSocket.
        //
        //     Cockpit takes :9090, its default — which is why Prometheus publishes on 9091.
        //     The package + password land HERE (before app units, so they exist even if a unit
        //     later fails), but the SOCKET is enabled further down, after units have restarted.
        //     Enabling it here fails with `Result: resources`: the app unit still holds :9090
        //     on its old published port until it restarts. Hit on CT 4001.
        if (Cockpit(s))
        {
            sb.Append("export DEBIAN_FRONTEND=noninteractive\n");
            sb.Append("if ! dpkg -s cockpit >/dev/null 2>&1; then apt-get update -qq && " +
                      "apt-get install -y -qq cockpit cockpit-podman; fi\n");
            if (cockpitPassword is { Length: > 0 })
                sb.Append($"printf '%s' '{user}:{cockpitPassword}' | chpasswd\n");
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

        // 9c. Cockpit's socket, LAST — after app units have rebound to their new ports.
        //     See 7d: binding :9090 before Prometheus moves to 9091 fails outright.
        if (Cockpit(s))
            sb.Append("systemctl enable --now cockpit.socket\n");

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
    // UserCmd on its own is NOT a runnable command.
    //
    // It interpolates $UID_N, which BuildDeploy defines in its preamble, and it relies on that
    // preamble's `cd /` because runuser keeps the caller's cwd and `pct exec` lands in /root, which
    // the rootless user cannot read. Run outside that script it produced XDG_RUNTIME_DIR=/run/user/
    // and podman failed with "mkdir /run/user/libpod: permission denied".
    //
    // So anything invoking UserCmd outside BuildDeploy has to carry the same preamble. The useradd
    // guard is idempotent and matches BuildDeploy's own.
    internal static string StandaloneUserCmd(string user, string cmd) =>
        string.Join("\n", new[]
        {
            "set -e",
            "cd /",
            $"id -u {user} >/dev/null 2>&1 || useradd -m -s /bin/bash {user}",
            $"UID_{"N"}=$(id -u {user})",
            UserCmd(user, cmd),
        });

    internal static string UserCmd(string user, string cmd) =>
        $"runuser -u {user} -- env XDG_RUNTIME_DIR=/run/user/$UID_N " +
        $"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$UID_N/bus {cmd}";

    // ── config accessors ───────────────────────────────────────────────────────────

    internal static string User(Shape s) => s.Spec.Config.Str("user") ?? DefaultUser;

    // Opt-in Cockpit management UI (ADR-0009 phase 3). Requires userSocket, since
    // cockpit-podman reads the same rootless API socket to list containers.
    internal static bool Cockpit(Shape s) =>
        s.Spec.Config.TryGetValue("cockpit", out var v) && v is not null
        && v.ToString() is not ("false" or "False" or "0");

    // Opt-in rootless API socket. Only hosts that run a metrics exporter need it.
    internal static bool UserSocket(Shape s) =>
        s.Spec.Config.TryGetValue("userSocket", out var v) && v is not null
        && v.ToString() is not ("false" or "False" or "0");

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

    // Every distinct Image= across the quadlets being deployed, in first-seen order.
    //
    // Deliberately a plain line scan rather than an ini parse: quadlet files are systemd units,
    // Image= only ever appears in [Container], and a .network or .volume file simply has none.
    internal static IReadOnlyList<string> QuadletImages(IEnumerable<string> files)
    {
        var seen = new List<string>();
        foreach (var f in files)
        {
            foreach (var raw in File.ReadLines(f))
            {
                var line = raw.Trim();
                if (!line.StartsWith("Image=", StringComparison.Ordinal)) continue;
                var image = line["Image=".Length..].Trim();
                // Skip a build-target reference (`Image=foo.build`), which has nothing to pull.
                if (image.Length == 0 || image.EndsWith(".build", StringComparison.Ordinal)) continue;
                if (!seen.Contains(image, StringComparer.Ordinal)) seen.Add(image);
            }
        }
        return seen;
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

    // ── assets (#303) ───────────────────────────────────────────────────────────────
    // An arbitrary file tree rendered onto the host for quadlets to bind-mount: config
    // files, Grafana provisioning/dashboards, a quote-free healthcheck script, and so on.
    //
    // WHY this exists: quadlets alone can't carry a stack's configuration. Before this, a
    // stack needing config files had to ship them by a SECOND mechanism running alongside
    // converge (the monitoring stack's own install.sh tars the tree into the CT), which
    // means two deploy paths and no single source of truth. It also unblocks the only
    // workable form of a healthcheck when the command needs quotes (gotcha #10): point
    // HealthCmd at a rendered script instead.
    //
    // Rendered per-file (not as a tarball) so the emitted script — and therefore the
    // managed marker — stays deterministic; tar embeds mtimes and would make the marker
    // change on every run.
    internal const string DefaultAssetsTargetSuffix = "assets";

    // Assets are pushed in their OWN chunked commands, NOT inside the deploy script.
    //
    // Learned the hard way on CT 4001 (#303): the monitoring tree is 286 KiB of base64
    // (a 100 KB generated snmp.yml + a 97 KB Grafana dashboard), and embedding that in the
    // single `pct exec` deploy script killed the SSH connection outright — "Connection reset
    // by peer", with NOTHING executed, not even the first useradd. Measured limit on this
    // path: 96 KiB of command line works, 128 KiB does not. Keepalives cannot help; it is a
    // command-length rejection, not a timeout.
    //
    // So each file is written in chunks well under that limit, one command per chunk
    // (`>` for the first, `>>` for the rest). Tree size then stops mattering at all.
    internal const int AssetChunkBytes = 32 * 1024;

    // Not a shell limit any more — just a sanity bound, since every chunk is a separate
    // round trip and a very large tree would be slow to ship this way.
    internal const int MaxAssetBytes = 16 * 1024 * 1024;

    internal static string? AssetsSourceDir(Shape s)
    {
        if (s.Spec.Config.Str("assets") is not { Length: > 0 } rel) return null;
        if (Path.IsPathRooted(rel)) return rel;
        return s.SourceDir is { Length: > 0 } dir ? Path.Combine(dir, rel) : null;
    }

    internal static string AssetsTarget(Shape s) =>
        s.Spec.Config.Str("assetsTarget") ?? $"/home/{User(s)}/{DefaultAssetsTargetSuffix}";

    // Relative paths under the assets dir, ordinal-sorted for a stable marker.
    internal static IReadOnlyList<string> AssetFiles(Shape s)
    {
        var dir = AssetsSourceDir(s);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
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

    // (relativePath, base64Content, isExecutable) for every asset file. `.sh` is rendered
    // executable; everything else 0644. Throws with a clear message if the tree is too big to
    // ship in one `pct exec` command line.
    internal static IReadOnlyList<(string Rel, string B64, bool Exec)> ReadAssets(Shape s)
    {
        var dir = AssetsSourceDir(s);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<(string, string, bool)>();

        var result = new List<(string, string, bool)>();
        long total = 0;
        foreach (var rel in AssetFiles(s))
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, rel));
            total += bytes.Length;
            if (total > MaxAssetBytes)
                throw new InvalidOperationException(
                    $"assets under '{dir}' exceed {MaxAssetBytes / 1024} KiB — they are delivered in a single " +
                    "pct exec command line and would overflow the shell's argument limit. Trim the tree, or " +
                    "fetch large artifacts on the host instead of rendering them.");
            result.Add((rel, Convert.ToBase64String(bytes), rel.EndsWith(".sh", StringComparison.Ordinal)));
        }
        return result;
    }

    private static string SafeRead(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
