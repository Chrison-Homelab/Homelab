using System.Text;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// RomM (CT 5110) — the app half of #485's OIDC migration. The IdP half (provider,
// application, group bindings and the `romm_roles` scope mapping) lives in the
// superproject blueprint; this points RomM at it.
//
// WHY A PROVISIONER AT ALL, when Pulse/Grafana/Forgejo needed none: those are podman
// guests whose environment is declared in a quadlet. RomM is a NATIVE community-scripts
// install (MariaDB + Angie + three systemd units), and its configuration is a plain
// KEY=value file at /opt/romm/.env that the installer wrote and nothing manages. Without
// this, RomM's half of the migration is a hand-edit — which is the thing the Audiobookshelf
// migration had to accept (#538) and this one does not have to.
//
// MERGE, NEVER REWRITE. That file already holds the DB password, ROMM_AUTH_SECRET_KEY and
// the scan schedule, none of which we know or should know. Every write preserves unmanaged
// keys, comments and ordering, and only the OIDC_* keys below are ours.
//
// ⚠ DISABLE_USERPASS_LOGIN IS DELIBERATELY NOT MANAGED. Leaving local login enabled is the
// break-glass rule every other client in this estate follows: OIDC is added as a realm, it
// never becomes the only one. RomM offers the switch and we decline it on purpose — if that
// ever changes it should be a visible decision in the shape, not a default that drifted in.
//
// ⚠ THE ROLE VALUES ARE COUPLED TO THE BLUEPRINT'S SCOPE MAPPING, ACROSS TWO REPOSITORIES.
// authentik emits `romm_roles: ["admin"]` or `["user"]` — collapsed there because RomM's
// OIDC_ROLE_* are each a SINGLE string tested with `in roles`, so only one group can mean
// admin while our model has two (homelab-admins as the superset, media-admins per stack).
// They are declared in the shape rather than hard-coded here so both ends of that contract
// are reviewable in a diff. Change one end and the other must move with it; a mismatch is
// not a downgrade but a 403:
//     else: raise HTTPException(403, "User has not been granted any roles for this application.")
//
// Idempotent the same way ShelfmarkProvisioner is: a read-only python pass reports
// CHANGED/NOCHANGE, and only on drift does it write and restart. No marker file — the
// desired state is fully readable from the env file itself, so the file IS the marker and
// a hand-edit is drift the next converge corrects rather than something a stale hash hides.
public sealed class RomMProvisioner : IAppProvisioner
{
    public string App => "romm";

    // Written by ct/romm.sh; read by all three romm-* units. Mode 600, root-owned.
    internal const string EnvFile = "/opt/romm/.env";

    // Only the backend serves the OIDC endpoints and reads the OIDC_* keys. The scheduler
    // and watcher load the same file but do library work, so restarting them would be
    // downtime bought for nothing.
    internal const string ServiceUnit = "romm-backend";

    internal const string ClientIdSecretKey = "AUTHENTIK_ROMM_CLIENT_ID";
    internal const string ClientSecretSecretKey = "AUTHENTIK_ROMM_CLIENT_SECRET";

    public IEnumerable<string> PlanSteps(Shape s)
    {
        if (IssuerUrl(s) is not { } issuer)
        {
            yield return "no config.oidcIssuerUrl — OIDC left unconfigured, RomM keeps local login only";
            yield break;
        }

        yield return $"merge OIDC_* into {EnvFile} → issuer {issuer}, claim {ClaimRoles(s)}, "
                   + $"admin '{RoleAdmin(s)}' / viewer '{RoleViewer(s)}' (other keys preserved)";
        yield return $"restart {ServiceUnit} only on drift, then assert is-active";
        yield return "DISABLE_USERPASS_LOGIN left untouched — local login stays as break-glass";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        var ct = CancellationToken.None;
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.Failed("missing node/ctid");

        if (IssuerUrl(s) is not { } issuer)
            return ApplyResult.NoChange("romm: no config.oidcIssuerUrl — OIDC not configured");

        if (RedirectUri(s) is not { } redirect)
            return ApplyResult.Failed(
                "config.oidcIssuerUrl is set but config.oidcRedirectUri is not. RomM sends an "
                + "absolute redirect_uri it does not derive, so leaving it unset produces an "
                + "invalid_redirect_uri at the end of an otherwise correct login.");

        var clientId = ctx.Secrets.Get(ClientIdSecretKey);
        var clientSecret = ctx.Secrets.Get(ClientSecretSecretKey);
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return ApplyResult.Failed(
                $"${ClientIdSecretKey} / ${ClientSecretSecretKey} must both be set. They are the "
                + "same pair authentik's blueprint pins via !Env, so a missing one here means the "
                + "two ends of the OIDC relationship disagree. Add them to secrets.env.template + "
                + "Bitwarden SM and run scripts/secrets-sync.sh.");

        var desired = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OIDC_ENABLED"] = "true",
            ["OIDC_PROVIDER"] = Provider(s),
            ["OIDC_CLIENT_ID"] = clientId!,
            ["OIDC_CLIENT_SECRET"] = clientSecret!,
            ["OIDC_REDIRECT_URI"] = redirect,
            ["OIDC_SERVER_APPLICATION_URL"] = issuer,
            ["OIDC_CLAIM_ROLES"] = ClaimRoles(s),
            ["OIDC_ROLE_ADMIN"] = RoleAdmin(s),
            ["OIDC_ROLE_VIEWER"] = RoleViewer(s),
            // Authentik's policy bindings already decide who may obtain a token at all, so an
            // account reaching this point has been admitted deliberately. Refusing to create it
            // here would mean every new household member needs a manual step in RomM as well.
            ["OIDC_ALLOW_REGISTRATION"] = "true",
        };

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildMerger(desired)));

        var check = await ctx.Exec.InContainerAsync(node, ctid, $"echo {b64} | base64 -d | python3 - check", ct);
        if (!check.Ok) return ApplyResult.Failed($"romm env check failed: {check.Stderr.Trim()} {check.Stdout.Trim()}");

        if (check.Stdout.Contains("NOCHANGE"))
        {
            var act0 = (await ctx.Exec.InContainerAsync(node, ctid, $"systemctl is-active {ServiceUnit}", ct)).Stdout.Trim();
            return act0 == "active"
                ? ApplyResult.NoChange($"romm OIDC current ({desired.Count} keys) + {ServiceUnit} active")
                : ApplyResult.Failed($"romm OIDC current but {ServiceUnit} not active (is-active: {act0})");
        }

        ctx.Report($"pointing RomM at {issuer} and restarting {ServiceUnit}");

        var write = await ctx.Exec.InContainerAsync(node, ctid,
            $"echo {b64} | base64 -d | python3 - write && systemctl restart {ServiceUnit} && sleep 4", ct);
        if (!write.Ok || !write.Stdout.Contains("WROTE"))
            return ApplyResult.Failed($"romm env merge/restart failed: {write.Stdout.Trim()} {write.Stderr.Trim()}");

        var act = (await ctx.Exec.InContainerAsync(node, ctid, $"systemctl is-active {ServiceUnit}", ct)).Stdout.Trim();
        if (act != "active")
        {
            var journal = await ctx.Exec.InContainerAsync(node, ctid,
                $"journalctl -u {ServiceUnit} --no-pager -n 20 2>/dev/null", ct);
            return ApplyResult.Failed(
                $"{ServiceUnit} not active after the OIDC merge (is-active: {act}) — journal:\n{journal.Stdout.Trim()}");
        }

        return ApplyResult.Applied($"romm OIDC configured against {issuer} + {ServiceUnit} restarted & active");
    }

    // The merger, as python rather than sed. A .env is KEY=value, and the values here include
    // a 128-character client secret — round-tripping that through shell quoting twice (once
    // for `pct exec`, once for sed's replacement, where & and \1 are special) is where this
    // would break silently. Python reads and writes the file itself, so nothing but the
    // base64'd spec crosses a shell.
    //
    // Rewrites keys IN PLACE and appends only genuinely new ones, so the installer's ordering
    // and comments survive and a diff of the file stays readable.
    internal static string BuildMerger(Dictionary<string, string> desired)
    {
        var specB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(desired)));
        return string.Join("\n", new[]
        {
            "import base64, json, os, sys",
            $"spec = json.loads(base64.b64decode(\"{specB64}\").decode())",
            $"p = \"{EnvFile}\"",
            // An absent env file means the install did not complete. Appending to a file that
            // is not there would create one RomM never reads and report success.
            "if not os.path.exists(p):",
            "    print('MISSING ' + p); sys.exit(1)",
            "lines = open(p).read().splitlines()",
            "seen, out, changed = set(), [], False",
            "for line in lines:",
            "    s = line.lstrip()",
            // Leave comments and blanks exactly as they are, including a commented-out
            // OIDC_ key — a commented key is not a set key, so it must not count as seen.
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
            "    out.append('# OIDC — managed by homelab converge (RomMProvisioner). Edits here are reverted.')",
            "    out.extend(k + '=' + spec[k] for k in missing)",
            "if changed and len(sys.argv) > 1 and sys.argv[1] == 'write':",
            // Write through a temp file in the same directory and replace atomically, so a
            // failure mid-write cannot leave RomM with a half-written env and no DB password.
            "    tmp = p + '.homelab.tmp'",
            "    with open(tmp, 'w') as f:",
            "        f.write('\\n'.join(out) + '\\n')",
            "    os.chmod(tmp, 0o600)",
            "    os.replace(tmp, p)",
            "    print('WROTE')",
            "else:",
            "    print('CHANGED' if changed else 'NOCHANGE')",
        });
    }

    // ── config accessors ────────────────────────────────────────────────────────────
    //
    // The issuer is the switch: absent means this guest keeps local login only, and the
    // provisioner is a no-op rather than a failure. That keeps the shape valid on a rebuild
    // before the IdP side exists.
    internal static string? IssuerUrl(Shape s) => s.Spec.Config.Str("oidcIssuerUrl");
    internal static string? RedirectUri(Shape s) => s.Spec.Config.Str("oidcRedirectUri");
    internal static string Provider(Shape s) => s.Spec.Config.Str("oidcProvider") ?? "authentik";
    internal static string ClaimRoles(Shape s) => s.Spec.Config.Str("oidcClaimRoles") ?? "romm_roles";
    internal static string RoleAdmin(Shape s) => s.Spec.Config.Str("oidcRoleAdmin") ?? "admin";
    internal static string RoleViewer(Shape s) => s.Spec.Config.Str("oidcRoleViewer") ?? "user";
}
