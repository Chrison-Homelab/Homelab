using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Unattended SECURITY updates inside every LXC (#436 follow-up, 2026-09-07).
//
// The September audit found ~300 pending OS packages across 31 guests — OpenSSL, libexpat,
// OpenSSH, PAM among them — and not one guest had unattended-upgrades installed, so nothing
// patched a library unless a person ran apt. This makes Debian's own mechanism part of the
// baseline every LXC converges to:
//
//   * `unattended-upgrades` installed with Allowed-Origins PINNED to the security pockets
//     (Debian's shipped default also admits the plain stable pocket) — non-security and
//     dist-upgrades stay a deliberate act (src/Proxmox/upgrade-guests.sh).
//   * Automatic-Reboot OFF. A reboot is a human's decision; the reboot-required flag shows up
//     in the dry-run report instead.
//   * The apt-daily timers enabled (they are on by default but nothing ran without the package).
//
// Idempotent: the package is installed only if missing, the two apt.conf.d files are rewritten
// only when their content differs (cmp), timers enabled only when not. Reports what changed.
// Runs before the app provisioner for every LXC member with a node + ctid that is RUNNING;
// a stopped guest is skipped, never started. Opt out per shape with
// `spec.config.securityUpdates: false` — deliberately loud in the plan when you do.
//
// Apps update through their own paths (community-scripts `update`, the arr built-in updater,
// podman auto-update); this never touches them. Not a converge of the node itself either.
public sealed class SecurityUpdatesReconciler
{
    public const string ConfigKey = "securityUpdates";
    private readonly INodeExec _exec;
    public SecurityUpdatesReconciler(INodeExec exec) => _exec = exec;

    public static bool Enabled(Shape s) =>
        !(s.Spec.Config.TryGetValue(ConfigKey, out var v) && v is not null
          && bool.TryParse(v.ToString(), out var b) && !b);

    public static string PlanStep(Shape s) => Enabled(s)
        ? "unattended security updates: ensure unattended-upgrades is installed + enabled (security pocket only, no auto-reboot)"
        : $"unattended security updates: OPTED OUT (config.{ConfigKey}: false)";

    public async Task<ApplyResult> ReconcileAsync(Shape s, CancellationToken ct = default)
    {
        if (!Enabled(s)) return ApplyResult.Skipped($"opted out (config.{ConfigKey}: false)");
        if (s.Spec.Node is not { } node || s.Spec.Ctid is not { } ctid)
            return ApplyResult.NoChange("no node/ctid");

        var st = await _exec.OnNodeAsync(node, $"pct status {ctid}", ct);
        if (!st.Ok) return ApplyResult.Failed($"pct status failed: {st.Stderr}");
        if (!st.Stdout.Contains("running", StringComparison.Ordinal))
            return ApplyResult.Skipped("CT not running — left alone");

        var r = await _exec.InContainerAsync(node, ctid, Script, ct);
        if (!r.Ok) return ApplyResult.Failed($"unattended-upgrades setup failed: {Truncate(r.Stderr.Length > 0 ? r.Stderr : r.Stdout)}");

        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        if (lines.Contains("not-apt")) return ApplyResult.Skipped("not an apt-based guest");
        var changedLine = lines.LastOrDefault(l => l.StartsWith("changed=", StringComparison.Ordinal));
        var changed = changedLine is not null && int.TryParse(changedLine["changed=".Length..], out var n) ? n : 0;
        var what = lines.Where(l => l != changedLine).ToList();
        return changed > 0
            ? ApplyResult.Applied($"unattended security updates: {string.Join(", ", what)}")
            : ApplyResult.NoChange("unattended security updates in place");
    }

    // The two managed files. 20auto-upgrades turns the daily run on; 52homelab pins the
    // behaviour we rely on, origins included.
    internal const string AutoUpgrades =
        "APT::Periodic::Update-Package-Lists \"1\";\n" +
        "APT::Periodic::Unattended-Upgrade \"1\";\n" +
        "APT::Periodic::AutocleanInterval \"7\";\n";

    internal const string Policy =
        "// homelab-managed (SecurityUpdatesReconciler). SECURITY POCKETS ONLY. Debian's shipped\n" +
        "// default also admits the plain stable pocket (point-release updates), so the list is\n" +
        "// cleared and pinned here; Ubuntu's security + ESM names are included so the same file\n" +
        "// is right on both. No automatic reboot: the reboot-required flag is reported by\n" +
        "// upgrade-guests.sh --dry-run and acted on by a person.\n" +
        "// Two lists, two grammars: Origins-Pattern takes key=value patterns (Debian's own default\n" +
        "// lives there), Allowed-Origins takes distro:archive (Ubuntu's). Mixing them makes\n" +
        "// unattended-upgrade abort at startup with 'Unable to parse Allowed-Origins'.\n" +
        "#clear Unattended-Upgrade::Allowed-Origins;\n" +
        "#clear Unattended-Upgrade::Origins-Pattern;\n" +
        "Unattended-Upgrade::Origins-Pattern {\n" +
        "  \"origin=Debian,codename=${distro_codename},label=Debian-Security\";\n" +
        "  \"origin=Debian,codename=${distro_codename}-security,label=Debian-Security\";\n" +
        "};\n" +
        "Unattended-Upgrade::Allowed-Origins {\n" +
        "  \"${distro_id}:${distro_codename}-security\";\n" +
        "  \"${distro_id}ESMApps:${distro_codename}-apps-security\";\n" +
        "  \"${distro_id}ESM:${distro_codename}-infra-security\";\n" +
        "};\n" +
        "Unattended-Upgrade::Automatic-Reboot \"false\";\n" +
        "Unattended-Upgrade::Remove-Unused-Dependencies \"true\";\n" +
        "Unattended-Upgrade::Remove-Unused-Kernel-Packages \"true\";\n" +
        "Unattended-Upgrade::MinimalSteps \"true\";\n" +
        "Unattended-Upgrade::SyslogEnable \"true\";\n";

    // Runs inside the CT. Prints one line per change and a final `changed=N`.
    internal static readonly string Script = string.Join("\n", new[]
    {
        "set -e",
        "command -v apt-get >/dev/null 2>&1 || { echo not-apt; echo changed=0; exit 0; }",
        "export DEBIAN_FRONTEND=noninteractive",
        "changed=0",
        "if ! dpkg -s unattended-upgrades >/dev/null 2>&1; then",
        "  apt-get -qq update >/dev/null 2>&1 || true",
        "  apt-get -qq -y install unattended-upgrades >/dev/null 2>&1",
        "  echo 'installed unattended-upgrades'; changed=$((changed+1))",
        "fi",
        "put() { f=$1; t=$(mktemp); cat > \"$t\"; if ! cmp -s \"$t\" \"$f\" 2>/dev/null; then install -m 0644 \"$t\" \"$f\"; echo \"wrote $f\"; changed=$((changed+1)); fi; rm -f \"$t\"; }",
        "put /etc/apt/apt.conf.d/20auto-upgrades <<'EOF_AU'",
        AutoUpgrades.TrimEnd('\n'),
        "EOF_AU",
        "put /etc/apt/apt.conf.d/52homelab-security-updates <<'EOF_POL'",
        Policy.TrimEnd('\n'),
        "EOF_POL",
        "for t in apt-daily.timer apt-daily-upgrade.timer; do",
        "  if ! systemctl is-enabled \"$t\" >/dev/null 2>&1 || ! systemctl is-active \"$t\" >/dev/null 2>&1; then",
        "    systemctl enable --now \"$t\" >/dev/null 2>&1 && { echo \"enabled $t\"; changed=$((changed+1)); }",
        "  fi",
        "done",
        "echo changed=$changed",
    });

    private static string Truncate(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
