using System.Security.Cryptography;
using System.Text;
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

    public IEnumerable<string> PlanSteps(Shape s)
    {
        if (SiteUrl(s) is { } url)
            yield return $"set site_url → {url} (replacing the DHCP address the installer wrote)";
        else
            yield return "no config.siteUrl — site_url left at whatever address the installer captured";

        yield return $"ensure superuser '{User(s)}' <{Email(s) ?? "no email"}> via admin_user/" +
                     $"admin_email/admin_password_file in {ConfigPath}";
        yield return $"write {PasswordFile} 0600 from ${PasswordSecretKey} (no trailing newline)";
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

        var marker = DesiredMarker(s, password);

        var cur = await ctx.Exec.InContainerAsync(node, ctid, $"cat {MarkerPath} 2>/dev/null || true");
        if (cur.Stdout.Trim() == marker)
            return ApplyResult.NoChange($"inventree current (marker {marker})");

        // The restart re-runs migrations and rebuilds the app state; on a cold guest that
        // is a minute of silence, which without a progress line is indistinguishable from
        // a hang (#369).
        ctx.Report($"configuring InvenTree ({SiteUrl(s) ?? "no site_url"}) and restarting {ServiceUnit}");

        var res = await ctx.Exec.InContainerAsync(node, ctid, BuildDeploy(s, marker, password));
        if (!res.Ok) return ApplyResult.Failed($"inventree configuration failed: {res.Stderr}");

        return ApplyResult.Applied(
            $"site_url {SiteUrl(s) ?? "(unset)"}, superuser '{User(s)}' ensured, {ServiceUnit} restarted (marker {marker})");
    }

    // ── the deploy script ───────────────────────────────────────────────────────────
    //
    // One `pct exec`. `set -e` throughout, marker stamped last.
    internal static string BuildDeploy(Shape s, string marker, string password)
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

        // The account is created by InvenTree's own startup hook, so the restart IS the step.
        sb.Append($"systemctl restart {ServiceUnit}; ");

        sb.Append($"printf '%s' {Sq(marker)} > {MarkerPath}");
        return sb.ToString();
    }

    // Hash the recipe alongside its inputs — a fixed script must re-converge on a host
    // that still carries the marker the broken script stamped.
    public static string DesiredMarker(Shape s, string password) =>
        Sha(string.Join('|', new[]
        {
            SiteUrl(s) ?? "(no site_url)",
            User(s),
            Email(s) ?? "(no email)",
            $"pw={Sha(password)[..16]}",
            Sha(BuildDeploy(s, "<marker>", "<password>")),
        }))[..12];

    internal static string? SiteUrl(Shape s) => s.Spec.Config.Str("siteUrl")?.TrimEnd('/');
    internal static string User(Shape s) => s.Spec.Config.Str("adminUser") ?? DefaultUser;
    internal static string? Email(Shape s) => s.Spec.Config.Str("adminEmail");

    // Single-quote for the remote shell. NodeExec already quotes the whole pct exec
    // payload once; this is the inner layer.
    private static string Sq(string v) => "'" + v.Replace("'", "'\\''") + "'";

    private static string Sha(string v) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v))).ToLowerInvariant();
}
