using System.Diagnostics;
using System.Text;
using Homelab.Infrastructure.Converge;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// RomM's OIDC half (#485). The interesting surface is the env merger, because it edits a
// file that already holds the DB password and ROMM_AUTH_SECRET_KEY — a merger that
// truncated, reordered or dropped an unmanaged key would break the guest in a way no unit
// test of the C# alone would notice.
//
// So these tests RUN the generated python against a fixture rather than asserting on its
// text. Asserting the script contains a substring proves nothing about what it does to a
// file; running it proves the thing that matters. They skip if python3 is unavailable —
// the converge host always has it (the provisioner would not work otherwise), and a
// developer machine without it should not fail the suite for an unrelated reason.
public sealed class RomMProvisionerTests
{
    private static Dictionary<string, string> Desired() => new(StringComparer.Ordinal)
    {
        ["OIDC_ENABLED"] = "true",
        ["OIDC_CLIENT_SECRET"] = new string('s', 128),
        ["OIDC_CLAIM_ROLES"] = "romm_roles",
    };

    // The installer's file, including the two shapes that trip naive mergers: a commented-out
    // OIDC_ key (present but NOT set) and a value containing characters sed would treat as
    // special.
    private const string Fixture = """
        ROMM_BASE_PATH=/var/lib/romm
        DB_PASSWD=p@ss&word\1with-specials
        ROMM_AUTH_SECRET_KEY=abc123
        # OIDC_ENABLED=false
        LOGLEVEL=INFO
        """;

    [Fact]
    public void Merger_AddsManagedKeysAndPreservesEverythingElse()
    {
        if (!HasPython(out _)) return;

        var (path, dir) = WriteFixture();
        try
        {
            Assert.Equal("CHANGED", RunMerger(path, "check").Trim());
            Assert.Equal("WROTE", RunMerger(path, "write").Trim());

            var after = File.ReadAllText(path);

            // Every unmanaged key survives, values intact — this is the whole point.
            Assert.Contains("DB_PASSWD=p@ss&word\\1with-specials", after, StringComparison.Ordinal);
            Assert.Contains("ROMM_AUTH_SECRET_KEY=abc123", after, StringComparison.Ordinal);
            Assert.Contains("LOGLEVEL=INFO", after, StringComparison.Ordinal);

            foreach (var (k, v) in Desired())
                Assert.Contains($"{k}={v}", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merger_IsIdempotent()
    {
        if (!HasPython(out _)) return;

        var (path, dir) = WriteFixture();
        try
        {
            RunMerger(path, "write");
            var first = File.ReadAllText(path);

            // A second pass must report NOCHANGE, or converge restarts romm-backend on every
            // run — the difference between a provisioner and a service bounce on a timer.
            Assert.Equal("NOCHANGE", RunMerger(path, "check").Trim());
            RunMerger(path, "write");
            Assert.Equal(first, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merger_TreatsACommentedKeyAsUnsetRatherThanSatisfied()
    {
        if (!HasPython(out _)) return;

        var (path, dir) = WriteFixture();
        try
        {
            RunMerger(path, "write");
            var lines = File.ReadAllLines(path);

            // The installer ships `# OIDC_ENABLED=false`. A merger that counted a commented
            // key as seen would leave it commented and never set the real one, producing a
            // guest whose config file mentions OIDC and whose OIDC is off.
            Assert.Contains(lines, l => l == "# OIDC_ENABLED=false");
            Assert.Contains(lines, l => l == "OIDC_ENABLED=true");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merger_RewritesInPlaceWithoutDuplicatingOnValueChange()
    {
        if (!HasPython(out _)) return;

        var (path, dir) = WriteFixture();
        try
        {
            RunMerger(path, "write");

            // Rotate the secret — the realistic change — and confirm the key is updated in
            // place rather than appended a second time. Two OIDC_CLIENT_SECRET lines would
            // leave RomM reading whichever its parser happened to prefer.
            var rotated = Desired();
            rotated["OIDC_CLIENT_SECRET"] = new string('r', 128);
            Assert.Equal("WROTE", RunMerger(path, "write", rotated).Trim());

            var lines = File.ReadAllLines(path);
            Assert.Single(lines, l => l.StartsWith("OIDC_CLIENT_SECRET=", StringComparison.Ordinal));
            Assert.Contains(lines, l => l == "OIDC_CLIENT_SECRET=" + new string('r', 128));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merger_FailsLoudlyWhenTheEnvFileIsAbsent()
    {
        if (!HasPython(out _)) return;

        var dir = Directory.CreateTempSubdirectory("rommtest").FullName;
        try
        {
            var missing = Path.Combine(dir, ".env");
            var (stdout, exit) = Run(missing, "check", Desired());

            // An absent env file means the install did not complete. Appending to a file that
            // is not there would create one RomM never reads and report success.
            Assert.NotEqual(0, exit);
            Assert.Contains("MISSING", stdout, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── config accessors ────────────────────────────────────────────────────────────

    [Fact]
    public void RoleDefaultsMatchTheBlueprintsScopeMapping()
    {
        var s = new Shapes.Shape();

        // These defaults are one end of a contract whose other end is the `romm_roles` scope
        // mapping in the superproject blueprint, which emits exactly "admin" or "user". A
        // mismatch is a 403 at login, not a lesser role — so drift here should fail a test
        // rather than a household member's login.
        Assert.Equal("romm_roles", RomMProvisioner.ClaimRoles(s));
        Assert.Equal("admin", RomMProvisioner.RoleAdmin(s));
        Assert.Equal("user", RomMProvisioner.RoleViewer(s));
        Assert.Equal("authentik", RomMProvisioner.Provider(s));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static (string Path, string Dir) WriteFixture()
    {
        var dir = Directory.CreateTempSubdirectory("rommtest").FullName;
        var path = Path.Combine(dir, ".env");
        File.WriteAllText(path, Fixture.ReplaceLineEndings("\n") + "\n");
        return (path, dir);
    }

    private static string RunMerger(string envPath, string mode, Dictionary<string, string>? desired = null) =>
        Run(envPath, mode, desired ?? Desired()).Stdout;

    private static (string Stdout, int Exit) Run(string envPath, string mode, Dictionary<string, string> desired)
    {
        // BuildMerger bakes in the real /opt/romm/.env path; point it at the fixture so the
        // script under test is the shipped one, not a re-implementation of it.
        var script = RomMProvisioner.BuildMerger(desired)
            .Replace($"\"{RomMProvisioner.EnvFile}\"", $"\"{envPath}\"", StringComparison.Ordinal);

        var file = Path.Combine(Path.GetDirectoryName(envPath)!, "merge.py");
        File.WriteAllText(file, script);

        HasPython(out var python);
        var psi = new ProcessStartInfo(python!, $"\"{file}\" {mode}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (stdout, p.ExitCode);
    }

    private static bool HasPython(out string? path)
    {
        foreach (var candidate in new[] { "python3", "/usr/bin/python3", "/usr/local/bin/python3" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "--version") { RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.WaitForExit();
                if (p.ExitCode == 0) { path = candidate; return true; }
            }
            catch { /* next candidate */ }
        }
        path = null;
        return false;
    }
}
