using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// InvenTree — the parts inventory (#508).
//
// CREATE half: `app: inventree` → ct/inventree.sh, which adds packager.io's deb repo,
// installs the package and runs `invoke update` (migrations + static). What it leaves
// behind is a running instance nobody can use:
//
//   1. `site_url` is set to the CT's DHCP ADDRESS. That is the hard-coded-address
//      failure the `.internal` DNS records exist to prevent — the next lease change
//      silently breaks every absolute link and the API's own notion of itself.
//   2. There is NO superuser. `invoke superuser` is Django's `createsuperuser`
//      WITHOUT --noinput (see InvenTree tasks.py), so it prompts — useless from
//      converge. Nothing can log in and no API token can be minted, which makes the
//      whole point of the guest — an agent querying stock against a BOM — unreachable.
//
// Both are closed here, declaratively, through the config file InvenTree already reads:
// `admin_user` / `admin_email` / `admin_password_file` are first-class config keys, and
// InvenTree's own startup hook (InvenTree/apps.py `_create_admin_user`) creates the
// account on the next boot — skipping it if the username already exists, so a restart
// is not a reset.
//
// ⚠ THE PASSWORD FILE MUST NOT END IN A NEWLINE. apps.py does
//   `add_password_file.read_text()` with NO strip, so a trailing \n becomes part of the
//   password and the account you just created will not accept the password you stored
//   in Bitwarden. Hence `printf '%s'`, never `echo`.
//
// SSO (#485) is optional and OFF unless the shape sets `config.ssoIssuer`. InvenTree logs in
// through django-allauth's openid_connect provider, configured by two NESTED config.yaml keys
// (`social_backends`, `social_providers`) that the line-based set_key below cannot write, so
// they go in as ONE managed block between marker comments, replaced wholesale on every run.
// Three things make that safe to do to a live config file:
//   - the client secret has no `_file` variant, so it lands INLINE in config.yaml, which the
//     packager leaves 0664 (readable by every account in the guest). The recipe asserts 0600.
//   - a foreign `social_backends:`/`social_providers:` key outside our block would be a
//     duplicate YAML key and break the whole app, so that is refused rather than appended over.
//   - the file is parsed with the app's own Python before the restart; on a parse failure the
//     backup is restored and nothing restarts, so a bad block cannot take InvenTree down.
// The two DB-backed settings that switch SSO on (LOGIN_ENABLE_SSO, LOGIN_ENABLE_SSO_REG) are not
// config-file keys, so they are set through the REST API with the admin account after the restart.
//
// Idempotent via a managed marker stamped LAST (mark-on-SUCCESS): a partial failure
// leaves no current marker, so the next converge re-runs the whole recipe. The marker
// covers the desired config values, a hash of the password, AND the generated script —
// so fixing a bug in the recipe below re-converges rather than silently no-opping
// against a host carrying the old marker (the trap PodmanProvisioner documents).
public sealed class InvenTreeProvisioner : IAppProvisioner
{
    public string App => "inventree";

    internal const string ConfigPath = "/etc/inventree/config.yaml";
    // Beside config.yaml because that whole directory is already the app's own
    // (chowned to inventree:inventree by the packager postinstall), and because
    // config.yaml is where InvenTree looks for the path by default.
    internal const string PasswordFile = "/etc/inventree/admin_password.txt";
    // The single systemd unit the packager.io install exposes (functions.sh
    // start_inventree → `systemctl start inventree`); it pulls in the web and worker units.
    internal const string ServiceUnit = "inventree";
    internal const string MarkerPath = "/etc/inventree/.homelab-managed";

    internal const string PasswordSecretKey = "INVENTREE_ADMIN_PASSWORD";
    internal const string DefaultUser = "admin";

    // The pair authentik's blueprint pins via !Env — the same two values, written from the
    // other end. Two-ended like the Forgejo and Grafana pairs.
    internal const string SsoClientIdKey = "AUTHENTIK_INVENTREE_CLIENT_ID";
    internal const string SsoClientSecretKey = "AUTHENTIK_INVENTREE_CLIENT_SECRET";
    // allauth builds the callback as /accounts/oidc/<provider_id>/login/callback/, and the authentik
    // blueprint's redirect URI is coupled to this id. Change one, change the other.
    internal const string DefaultSsoProviderId = "authentik";
    // The prefixes are what sed matches on, so they must contain nothing a BRE treats specially. An
    // earlier draft ran the full begin line through Regex.Escape, which writes `\(` — a capture
    // GROUP in sed, not a literal paren — so the old block was never found and the second converge
    // refused its own previous block as a "foreign" key.
    internal const string SsoBlockBeginPrefix = "# >>> homelab-managed sso";
    internal const string SsoBlockBegin = SsoBlockBeginPrefix + " (do not edit; replaced on every converge)";
    internal const string SsoBlockEnd = "# <<< homelab-managed sso";

    internal readonly record struct SsoCredentials(string ClientId, string ClientSecret);

    public IEnumerable<string> PlanSteps(Shape s)
    {
        if (SiteUrl(s) is { } url)
            yield return $"set site_url → {url} (replacing the DHCP address the installer wrote)";
        else
            yield return "no config.siteUrl — site_url left at whatever address the installer captured";

        yield return $"ensure superuser '{User(s)}' <{Email(s) ?? "no email"}> via admin_user/" +
                     $"admin_email/admin_password_file in {ConfigPath}";
        yield return $"write {PasswordFile} 0600 from ${PasswordSecretKey} (no trailing newline)";

        if (SsoIssuer(s) is { } issuer)
        {
            yield return $"configure SSO provider '{SsoProviderId(s)}' ({issuer}) as a managed block in {ConfigPath}, " +
                         $"client pair from ${SsoClientIdKey}/${SsoClientSecretKey}, file forced to 0600";
            yield return $"validate {ConfigPath} with the app's Python before restarting; restore the backup on failure";
            yield return "enable LOGIN_ENABLE_SSO and LOGIN_ENABLE_SSO_REG through the REST API (DB-backed settings)";
        }

        yield return $"restart {ServiceUnit} so InvenTree's startup hook creates the account";
    }

    public async Task<ApplyResult> ApplyAsync(Shape s, ConvergeContext ctx)
    {
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.Failed("missing node/ctid");

        var password = ctx.Secrets.Get(PasswordSecretKey);
        if (string.IsNullOrEmpty(password))
            return ApplyResult.Failed(
                $"${PasswordSecretKey} is not set. Without it there is no superuser, so nothing " +
                "can log in and no API token can be minted — which is the entire purpose of this " +
                "guest. Add it to secrets.env.template + Bitwarden SM and run scripts/secrets-sync.sh.");

        SsoCredentials? sso = null;
        if (SsoIssuer(s) is not null)
        {
            var clientId = ctx.Secrets.Get(SsoClientIdKey);
            var clientSecret = ctx.Secrets.Get(SsoClientSecretKey);
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                return ApplyResult.Failed(
                    $"config.ssoIssuer is set but ${SsoClientIdKey} / ${SsoClientSecretKey} are not both set. " +
                    "Without the pair InvenTree would be configured with a provider that can never log anyone in. " +
                    "They are generated into OpenBao for the authentik blueprint; run scripts/secrets-sync.sh.");
            sso = new SsoCredentials(clientId, clientSecret);
        }

        var marker = DesiredMarker(s, password, sso);

        var cur = await ctx.Exec.InContainerAsync(node, ctid, $"cat {MarkerPath} 2>/dev/null || true");
        if (cur.Stdout.Trim() == marker)
            return ApplyResult.NoChange($"inventree current (marker {marker})");

        // The restart re-runs migrations and rebuilds the app state; on a cold guest that
        // is a minute of silence, which without a progress line is indistinguishable from
        // a hang (#369).
        ctx.Report($"configuring InvenTree ({SiteUrl(s) ?? "no site_url"}) and restarting {ServiceUnit}");

        var res = await ctx.Exec.InContainerAsync(node, ctid, BuildDeploy(s, marker, password, sso));
        if (!res.Ok) return ApplyResult.Failed($"inventree configuration failed: {res.Stderr}");

        var ssoNote = sso is null ? "" : $", SSO provider '{SsoProviderId(s)}' configured";
        return ApplyResult.Applied(
            $"site_url {SiteUrl(s) ?? "(unset)"}, superuser '{User(s)}' ensured{ssoNote}, {ServiceUnit} restarted (marker {marker})");
    }

    // ── the deploy script ───────────────────────────────────────────────────────────
    //
    // One `pct exec`. `set -e` throughout, marker stamped last.
    internal static string BuildDeploy(Shape s, string marker, string password, SsoCredentials? sso = null)
    {
        var sb = new StringBuilder();
        sb.Append("set -e; ");

        // Fail loudly rather than creating a config file InvenTree does not read. An absent
        // config.yaml means the package install did not complete, and appending keys to a
        // file that isn't there would look like success.
        sb.Append($"test -f {ConfigPath} || {{ echo 'no {ConfigPath} — is InvenTree installed?' >&2; exit 1; }}; ");

        // printf, NOT echo — see the newline warning in the class comment.
        //
        // chmod EXPLICITLY rather than leaning on `umask 077` before the redirect. A umask
        // only applies when the redirect CREATES the file, so a re-converge over an existing
        // file inherits whatever mode it already had — and the first run of this recipe did
        // land 0644, leaving the superuser password readable by every account in the guest.
        // A mode this file must have is a thing to assert, not to arrange ambiently.
        sb.Append($"printf '%s' {Sq(password)} > {PasswordFile}; ");
        sb.Append($"chmod 600 {PasswordFile}; ");
        sb.Append($"chown inventree:inventree {PasswordFile} 2>/dev/null || true; ");

        // Set a top-level scalar whether the template has it commented out (`#admin_user: admin`),
        // set already, or missing entirely. Anchored on the key + colon so admin_password_file
        // is never matched by the admin_password pattern, and vice versa.
        sb.Append("set_key() { k=\"$1\"; v=\"$2\"; ");
        sb.Append($"if grep -qE \"^[#[:space:]]*${{k}}:\" {ConfigPath}; ");
        sb.Append($"then sed -i -E \"s|^[#[:space:]]*${{k}}:.*|${{k}}: ${{v}}|\" {ConfigPath}; ");
        sb.Append($"else printf '%s: %s\\n' \"$k\" \"$v\" >> {ConfigPath}; fi; }}; ");

        if (SiteUrl(s) is { } url) sb.Append($"set_key site_url {Sq(url)}; ");
        sb.Append($"set_key admin_user {Sq(User(s))}; ");
        if (Email(s) is { } email) sb.Append($"set_key admin_email {Sq(email)}; ");
        sb.Append($"set_key admin_password_file {Sq(PasswordFile)}; ");

        if (sso is { } creds) AppendSso(sb, s, password, creds);

        // The account is created by InvenTree's own startup hook, so the restart IS the step.
        sb.Append($"systemctl restart {ServiceUnit}; ");

        if (sso is not null) AppendEnableSso(sb, s, password);

        sb.Append($"printf '%s' {Sq(marker)} > {MarkerPath}");
        return sb.ToString();
    }

    // The managed config block, then the guards around it. Order matters: back up, drop our old
    // block, refuse foreign keys, append, force 0600, validate — and only a valid file goes on to
    // the restart. `set -e` is already on, so a failed validate restores the backup explicitly
    // BEFORE exiting rather than leaving a broken file for the next restart to trip over.
    private static void AppendSso(StringBuilder sb, Shape s, string password, SsoCredentials creds)
    {
        var bak = ConfigPath + ".homelab-bak";
        sb.Append($"cp -p {ConfigPath} {bak}; ");
        sb.Append($"sed -i '/^{SsoBlockBeginPrefix}/,/^{SsoBlockEnd}/d' {ConfigPath}; ");
        sb.Append($"if grep -qE '^(social_backends|social_providers):' {ConfigPath}; then ");
        sb.Append($"mv {bak} {ConfigPath}; ");
        sb.Append($"echo 'social_backends/social_providers already set outside the homelab-managed block in {ConfigPath} — refusing to add a duplicate YAML key' >&2; exit 1; fi; ");
        sb.Append("printf '%s\\n' " + string.Join(' ', SsoBlockLines(s, creds).Select(Sq)) + $" >> {ConfigPath}; ");
        // 0664 as shipped; this file now holds a client secret. Assert it, as for the password file.
        sb.Append($"chmod 600 {ConfigPath}; chown inventree:inventree {ConfigPath} 2>/dev/null || true; ");
        sb.Append($"if ! {VenvPython} -c 'import yaml,sys; yaml.safe_load(open(sys.argv[1]))' {ConfigPath}; then ");
        sb.Append($"mv {bak} {ConfigPath}; echo '{ConfigPath} does not parse after adding the SSO block — restored the backup, nothing restarted' >&2; exit 1; fi; ");
    }

    // After the restart. InvenTree needs a while to come back (migrations), so poll rather than
    // sleeping a guess. Credentials go to curl on stdin, never argv, so they are not in `ps`.
    private static void AppendEnableSso(StringBuilder sb, Shape s, string password)
    {
        var baseUrl = SiteUrl(s) ?? "http://127.0.0.1";
        sb.Append("i=0; ");
        sb.Append($"until curl -fsS -o /dev/null {Sq(baseUrl + "/api/")}; do i=$((i+1)); ");
        sb.Append("if [ \"$i\" -ge 60 ]; then ");
        sb.Append($"echo '{ServiceUnit} did not answer on {baseUrl}/api/ within 3 minutes' >&2; exit 1; fi; sleep 3; done; ");
        foreach (var key in new[] { "LOGIN_ENABLE_SSO", "LOGIN_ENABLE_SSO_REG" })
        {
            sb.Append($"printf 'user = \"%s:%s\"\\n' {Sq(CurlCfg(User(s)))} {Sq(CurlCfg(password))} | ");
            sb.Append("curl -fsS -K - -X PATCH -H 'Content-Type: application/json' -d '{\"value\": true}' ");
            sb.Append($"-o /dev/null {Sq(baseUrl + "/api/settings/global/" + key + "/")}; ");
        }
    }

    // YAML block for the config file, one string per line. Values are single-quoted YAML scalars,
    // so only `'` needs doubling. The lines are passed to printf as separate arguments rather than
    // joined with newlines, so the command never contains a raw newline on its way through
    // ssh and `pct exec`.
    internal static string[] SsoBlockLines(Shape s, SsoCredentials creds)
    {
        static string Y(string v) => "'" + v.Replace("'", "''") + "'";
        var discovery = SsoIssuer(s) + "/.well-known/openid-configuration";
        return new[]
        {
            SsoBlockBegin,
            "social_backends:",
            "  - allauth.socialaccount.providers.openid_connect",
            "social_providers:",
            "  openid_connect:",
            "    APPS:",
            "      - provider_id: " + Y(SsoProviderId(s)),
            "        name: " + Y(SsoName(s)),
            "        client_id: " + Y(creds.ClientId),
            "        secret: " + Y(creds.ClientSecret),
            "        settings:",
            "          server_url: " + Y(discovery),
            SsoBlockEnd,
        };
    }

    // Hash the recipe alongside its inputs — a fixed script must re-converge on a host
    // that still carries the marker the broken script stamped.
    internal static string DesiredMarker(Shape s, string password, SsoCredentials? sso = null) =>
        Sha(string.Join('|', new[]
        {
            SiteUrl(s) ?? "(no site_url)",
            User(s),
            Email(s) ?? "(no email)",
            $"pw={Sha(password)[..16]}",
            sso is { } c
                ? $"sso={SsoIssuer(s)}|{SsoProviderId(s)}|{SsoName(s)}|{Sha(c.ClientId + ":" + c.ClientSecret)[..16]}"
                : "sso=off",
            Sha(BuildDeploy(s, "<marker>", "<password>", sso is null ? null : new SsoCredentials("<id>", "<secret>"))),
        }))[..12];

    internal static string? SiteUrl(Shape s) => s.Spec.Config.Str("siteUrl")?.TrimEnd('/');
    internal static string User(Shape s) => s.Spec.Config.Str("adminUser") ?? DefaultUser;
    internal static string? Email(Shape s) => s.Spec.Config.Str("adminEmail");

    // SSO is opt-in: no ssoIssuer, no SSO, and every existing shape behaves exactly as before.
    internal static string? SsoIssuer(Shape s)
    {
        var v = s.Spec.Config.Str("ssoIssuer")?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(v)) return null;
        if (!v.StartsWith("https://", StringComparison.Ordinal))
            throw new InvalidOperationException($"config.ssoIssuer must be an https:// URL, got '{v}'");
        return v;
    }
    internal static string SsoProviderId(Shape s)
    {
        var v = s.Spec.Config.Str("ssoProviderId") ?? DefaultSsoProviderId;
        // It becomes a URL path segment (/accounts/oidc/<id>/login/callback/) and a YAML value.
        if (!Regex.IsMatch(v, "^[A-Za-z0-9_-]+$"))
            throw new InvalidOperationException($"config.ssoProviderId must match [A-Za-z0-9_-]+, got '{v}'");
        return v;
    }
    internal static string SsoName(Shape s) => s.Spec.Config.Str("ssoName") ?? SsoProviderId(s);

    // Single-quote for the remote shell. NodeExec already quotes the whole pct exec
    // payload once; this is the inner layer.
    private static string Sq(string v) => "'" + v.Replace("'", "'\\''") + "'";

    // curl -K config strings are double-quoted, with \ and \" as the only escapes.
    private static string CurlCfg(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private const string VenvPython = "/opt/inventree/env/bin/python";

    private static string Sha(string v) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v))).ToLowerInvariant();
}
