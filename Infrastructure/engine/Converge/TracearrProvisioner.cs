using System.Text;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Tracearr (CT 5109) — the app half of #485's OIDC migration. The IdP half (provider,
// application and the homelab-admins binding) lives in the superproject blueprint; this
// points Tracearr at it.
//
// WHY A PROVISIONER: Tracearr is a NATIVE community-scripts install (Node + PostgreSQL +
// Redis + one systemd unit), and its configuration is a plain KEY=value file at
// /data/tracearr/.env that the installer wrote and nothing manages. Upstream reads OIDC
// from environment variables only (OIDC_ISSUER_URL / OIDC_CLIENT_ID / OIDC_CLIENT_SECRET,
// optional OIDC_PROVIDER_NAME), and enables OIDC only when all three are set — so unlike
// Audiobookshelf or Shelfmark this half CAN be declarative.
//
// MERGE, NEVER REWRITE. That file holds the DB URL, JWT_SECRET and COOKIE_SECRET, none of
// which we know or should know. Every write preserves unmanaged keys, comments and
// ordering; only the four OIDC_* keys below are ours.
//
// ⚠ THE FILE'S OWNER AND MODE MUST SURVIVE THE WRITE. It is tracearr:tracearr 0600 and the
// unit runs as User=tracearr. RomM's merger writes a root-owned temp file and renames it
// over the original, which is harmless there because RomM runs as root; here it would leave
// a root:root 0600 file the service cannot read, and tracearr would fail to start with the
// OIDC change as the only visible difference. The merger therefore copies uid/gid/mode from
// the file it replaces.
//
// ⚠ WHAT OIDC DOES AND DOES NOT DO HERE (read from the 2.4.1 server source, 2026-10-11).
// Tracearr is single-owner. Every newly created user is made `owner`, and signup is refused
// once an owner exists ("Only the owner can log in"), so an SSO login can never create an
// account on this instance. It can only LINK to the existing owner: accountLinking trusts
// the `oidc` provider and matches by email. The authentik account that signs in must
// therefore carry the same email as the Tracearr owner, or the login is refused. The
// blueprint binds only homelab-admins for the same reason: nobody else could use the app.
//
// Local login is left alone — nothing here disables it, so OIDC is added as a second way
// in rather than becoming the only one (the break-glass rule every client in this estate
// follows). Recovery if OIDC is misconfigured: upstream's `enable-local-login` command.
//
// Idempotent the same way RomMProvisioner is: a read-only python pass reports
// CHANGED/NOCHANGE, and only on drift does it write and restart. No marker file — the file
// IS the marker, so a hand-edit is drift the next converge corrects.
public sealed class TracearrProvisioner : IAppProvisioner
{
    public string App => "tracearr";

    // Written by ct/tracearr.sh and loaded by the unit via EnvironmentFile=. tracearr:tracearr 0600.
    internal const string EnvFile = "/data/tracearr/.env";

    internal const string ServiceUnit = "tracearr";

    internal const string ClientIdSecretKey = "AUTHENTIK_TRACEARR_CLIENT_ID";
    internal const string ClientSecretSecretKey = "AUTHENTIK_TRACEARR_CLIENT_SECRET";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        if (IssuerUrl(s) is not { } issuer)
        {
            yield return "no config.oidcIssuerUrl — OIDC left unconfigured, Tracearr keeps local/Plex login only";
            yield break;
        }

        yield return $"merge OIDC_* into {EnvFile} → issuer {issuer}, button '{ProviderName(s)}' "
                   + "(other keys preserved; owner/mode of the file preserved)";
        yield return $"restart {ServiceUnit} only on drift, then assert is-active";
        yield return "local login left untouched — stays as break-glass";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.Failed("missing node/ctid");

        if (IssuerUrl(s) is not { } issuer)
            return ApplyResult.NoChange("tracearr: no config.oidcIssuerUrl — OIDC not configured");

        var clientId = ctx.Secrets.Get(ClientIdSecretKey);
        var clientSecret = ctx.Secrets.Get(ClientSecretSecretKey);
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return ApplyResult.Failed(
                $"${ClientIdSecretKey} / ${ClientSecretSecretKey} must both be set. They are the "
                + "same pair authentik's blueprint pins via !Env, so a missing one here means the "
                + "two ends of the OIDC relationship disagree. Add them to secrets.env.template and "
                + "OpenBao (scripts/openbao-set.sh).");

        var desired = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OIDC_ISSUER_URL"] = issuer,
            ["OIDC_CLIENT_ID"] = clientId!,
            ["OIDC_CLIENT_SECRET"] = clientSecret!,
            ["OIDC_PROVIDER_NAME"] = ProviderName(s),
        };

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildMerger(desired)));

        var check = await ctx.Exec.InContainerAsync(node, ctid, $"echo {b64} | base64 -d | python3 - check", ct);
        if (!check.Ok) return ApplyResult.Failed($"tracearr env check failed: {check.Stderr.Trim()} {check.Stdout.Trim()}");

        if (check.Stdout.Contains("NOCHANGE"))
        {
            var act0 = (await ctx.Exec.InContainerAsync(node, ctid, $"systemctl is-active {ServiceUnit}", ct)).Stdout.Trim();
            return act0 == "active"
                ? ApplyResult.NoChange($"tracearr OIDC current ({desired.Count} keys) + {ServiceUnit} active")
                : ApplyResult.Failed($"tracearr OIDC current but {ServiceUnit} not active (is-active: {act0})");
        }

        ctx.Report($"pointing Tracearr at {issuer} and restarting {ServiceUnit}");

        var write = await ctx.Exec.InContainerAsync(node, ctid,
            $"echo {b64} | base64 -d | python3 - write && systemctl restart {ServiceUnit} && sleep 5", ct);
        if (!write.Ok || !write.Stdout.Contains("WROTE"))
            return ApplyResult.Failed($"tracearr env merge/restart failed: {write.Stdout.Trim()} {write.Stderr.Trim()}");

        var act = (await ctx.Exec.InContainerAsync(node, ctid, $"systemctl is-active {ServiceUnit}", ct)).Stdout.Trim();
        if (act != "active")
        {
            var journal = await ctx.Exec.InContainerAsync(node, ctid,
                $"journalctl -u {ServiceUnit} --no-pager -n 20 2>/dev/null", ct);
            return ApplyResult.Failed(
                $"{ServiceUnit} not active after the OIDC merge (is-active: {act}) — journal:\n{journal.Stdout.Trim()}");
        }

        return ApplyResult.Applied($"tracearr OIDC configured against {issuer} + {ServiceUnit} restarted & active");
    }

    // The merger, as python rather than sed: values include a 64-character client secret,
    // and round-tripping that through shell quoting twice is where this would break
    // silently. Python reads and writes the file itself, so only the base64'd spec crosses
    // a shell. Rewrites keys IN PLACE and appends only genuinely new ones.
    internal static string BuildMerger(Dictionary<string, string> desired)
    {
        var specB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(desired)));
        return string.Join("\n", new[]
        {
            "import base64, json, os, sys",
            $"spec = json.loads(base64.b64decode(\"{specB64}\").decode())",
            $"p = \"{EnvFile}\"",
            "if not os.path.exists(p):",
            "    print('MISSING ' + p); sys.exit(1)",
            "st = os.stat(p)",
            "lines = open(p).read().splitlines()",
            "seen, out, changed = set(), [], False",
            "for line in lines:",
            "    s = line.lstrip()",
            // A commented key is not a set key, so it must not count as seen. The installer
            // ships `#CORS_ORIGIN=` this way.
            "    if not s or s.startswith('#') or '=' not in s:",
            "        out.append(line); continue",
            "    k = s.split('=', 1)[0].strip()",
            "    if k in spec:",
            "        seen.add(k)",
            "        new = k + '=' + spec[k]",
            "        if new != line:",
            "            changed = True",
            "        out.append(new)",
            "    else:",
            "        out.append(line)",
            "missing = [k for k in spec if k not in seen]",
            "if missing:",
            "    changed = True",
            "    out.append('')",
            "    out.append('# OIDC — managed by homelab converge (TracearrProvisioner). Edits here are reverted.')",
            "    out.extend(k + '=' + spec[k] for k in missing)",
            "if changed and len(sys.argv) > 1 and sys.argv[1] == 'write':",
            // Atomic replace via a temp file in the same directory, so a failure mid-write
            // cannot leave Tracearr with a half-written env. Owner and mode are copied from
            // the original BEFORE the rename: the unit runs as `tracearr`, and a root-owned
            // 0600 replacement would be unreadable to it.
            "    tmp = p + '.homelab.tmp'",
            "    with open(tmp, 'w') as f:",
            "        f.write('\\n'.join(out) + '\\n')",
            "    os.chmod(tmp, st.st_mode & 0o7777)",
            "    os.chown(tmp, st.st_uid, st.st_gid)",
            "    os.replace(tmp, p)",
            "    print('WROTE')",
            "else:",
            "    print('CHANGED' if changed else 'NOCHANGE')",
        });
    }

    // ── config accessors ────────────────────────────────────────────────────────────
    //
    // The issuer is the switch: absent means this guest keeps its own login only, and the
    // provisioner is a no-op rather than a failure, so the shape stays valid on a rebuild
    // before the IdP side exists. There is no redirect URI key: Tracearr derives it from the
    // request host (/api/v1/auth/oauth2/callback/oidc), so the blueprint's strict entry is
    // the only place it is written.
    internal static string? IssuerUrl(Shape s) => s.Spec.Config.Str("oidcIssuerUrl");
    internal static string ProviderName(Shape s) => s.Spec.Config.Str("oidcProviderName") ?? "authentik";
}
