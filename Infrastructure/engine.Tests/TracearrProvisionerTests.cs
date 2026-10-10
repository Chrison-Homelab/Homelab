using System.Diagnostics;
using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Tracearr's OIDC half (#485). As with RomM, the surface that matters is the env merger,
// because it edits a file holding JWT_SECRET, COOKIE_SECRET and the DB URL. These tests RUN
// the generated python against a fixture rather than asserting on its text; they skip if
// python3 is unavailable.
//
// The case that is new compared with RomM is OWNERSHIP: the env file is tracearr:tracearr
// 0600 and the service runs as that user, so a merger that replaces it with a root-owned
// file would break startup. The mode half of that is asserted here; the uid/gid half cannot
// be (the test does not run as root) but is exercised by the same chown call on a file the
// test user owns.
public sealed class TracearrProvisionerTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static Dictionary<string, string> Desired() => new(StringComparer.Ordinal)
    {
        ["OIDC_ISSUER_URL"] = "https://identity.chrison.dev/application/o/tracearr/",
        ["OIDC_CLIENT_ID"] = "tracearr",
        ["OIDC_CLIENT_SECRET"] = Secret,
        ["OIDC_PROVIDER_NAME"] = "authentik",
    };

    // The installer's file, including the shapes that trip naive mergers: a commented-out
    // key (present but NOT set) and a value containing characters sed treats as special.
    private const string Fixture = """
        DATABASE_URL=postgresql://tracearr:p@ss&word\1x@localhost:5432/tracearr_db
        REDIS_URL=redis://localhost:6379
        PORT=3000
        HOST=0.0.0.0
        JWT_SECRET=jwt-abc
        COOKIE_SECRET=cookie-def
        APP_VERSION=2.4.1
        #CORS_ORIGIN=
        """;

    private static Shape TracearrShape(string? issuer = "https://identity.chrison.dev/application/o/tracearr/")
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "tracearr", Stack = "Media" } };
        s.Spec.Node = "hpe-01";
        s.Spec.Ctid = "5109";
        s.Spec.App = "tracearr";
        if (issuer is not null) s.Spec.Config["oidcIssuerUrl"] = issuer;
        return s;
    }

    // ── merger ──────────────────────────────────────────────────────────────────────

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
            Assert.Contains("DATABASE_URL=postgresql://tracearr:p@ss&word\\1x@localhost:5432/tracearr_db", after, StringComparison.Ordinal);
            Assert.Contains("JWT_SECRET=jwt-abc", after, StringComparison.Ordinal);
            Assert.Contains("COOKIE_SECRET=cookie-def", after, StringComparison.Ordinal);
            Assert.Contains("APP_VERSION=2.4.1", after, StringComparison.Ordinal);
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

            // A second pass must say NOCHANGE, or converge restarts tracearr on every run.
            Assert.Equal("NOCHANGE", RunMerger(path, "check").Trim());
            RunMerger(path, "write");
            Assert.Equal(first, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merger_PreservesTheFileMode()
    {
        if (!HasPython(out _) || OperatingSystem.IsWindows()) return;
        var (path, dir) = WriteFixture();
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var before = File.GetUnixFileMode(path);

            Assert.Equal("WROTE", RunMerger(path, "write").Trim());

            // The real file is tracearr:tracearr 0600. A replacement that came out 0644 would
            // expose JWT_SECRET to other users in the CT; one that came out unreadable would
            // stop the service. The mode must be exactly what it was.
            Assert.Equal(before, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merger_KeepsTheOriginalFilesOwner()
    {
        if (!HasPython(out var python) || OperatingSystem.IsWindows()) return;
        var (path, dir) = WriteFixture();
        try
        {
            string Owner() => Capture(python!, "-c \"import os,sys;s=os.stat(sys.argv[1]);print(s.st_uid,s.st_gid)\" \"" + path + "\"").Trim();
            var before = Owner();
            RunMerger(path, "write");
            Assert.Equal(before, Owner());
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
            // The installer ships `#CORS_ORIGIN=`. The merger must leave a commented line
            // exactly as it is, and must not mistake a commented line for a set key.
            var spec = Desired();
            spec["CORS_ORIGIN"] = "http://tracearr.homelab.chrison.internal:3000";
            RunMerger(path, "write", spec);

            var lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l == "#CORS_ORIGIN=");
            Assert.Contains(lines, l => l == "CORS_ORIGIN=http://tracearr.homelab.chrison.internal:3000");
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

            var rotated = Desired();
            rotated["OIDC_CLIENT_SECRET"] = new string('r', 64);
            Assert.Equal("WROTE", RunMerger(path, "write", rotated).Trim());

            // Two OIDC_CLIENT_SECRET lines would leave Tracearr reading whichever its loader
            // preferred, so a rotation would look applied and not be.
            var lines = File.ReadAllLines(path);
            Assert.Single(lines, l => l.StartsWith("OIDC_CLIENT_SECRET=", StringComparison.Ordinal));
            Assert.Contains(lines, l => l == "OIDC_CLIENT_SECRET=" + new string('r', 64));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merger_FailsLoudlyWhenTheEnvFileIsAbsent()
    {
        if (!HasPython(out _)) return;
        var dir = Directory.CreateTempSubdirectory("tracearrtest").FullName;
        try
        {
            var (stdout, exit) = Run(Path.Combine(dir, ".env"), "check", Desired());
            Assert.NotEqual(0, exit);
            Assert.Contains("MISSING", stdout, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── apply ───────────────────────────────────────────────────────────────────────

    private sealed class FakeNodeExec : INodeExec
    {
        private readonly Func<string, ExecResult> _reply;
        public List<string> Commands { get; } = new();
        public FakeNodeExec(Func<string, ExecResult> reply) => _reply = reply;
        public Task<ExecResult> OnNodeAsync(string node, string command, CancellationToken ct = default)
        { Commands.Add(command); return Task.FromResult(_reply(command)); }
        public Task<ExecResult> InContainerAsync(string node, string ctid, string command, CancellationToken ct = default)
        { Commands.Add(command); return Task.FromResult(_reply(command)); }
    }

    private static ConvergeContext Ctx(INodeExec exec, string? id = "tracearr", string? secret = Secret)
    {
        Environment.SetEnvironmentVariable(TracearrProvisioner.ClientIdSecretKey, id);
        Environment.SetEnvironmentVariable(TracearrProvisioner.ClientSecretSecretKey, secret);
        return new(exec, SecretsEnv.Load(null), new Dictionary<string, Shape>(), Deriver: null!);
    }

    [Fact]
    public void Registry_DispatchesTracearrProvisioner_ByApp()
    {
        Assert.IsType<TracearrProvisioner>(ProvisionerRegistry.Default().For("tracearr"));
    }

    [Fact]
    public async Task WithoutAnIssuer_ItIsANoOpNotAFailure()
    {
        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));
        var r = await new TracearrProvisioner().ApplyAsync(TracearrShape(issuer: null), Ctx(exec));

        // A rebuild before the IdP side exists must still converge.
        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
        Assert.Empty(exec.Commands);
    }

    [Fact]
    public async Task MissingHalfOfThePair_FailsBeforeTouchingTheGuest()
    {
        var exec = new FakeNodeExec(_ => new ExecResult(0, "", ""));
        var r = await new TracearrProvisioner().ApplyAsync(TracearrShape(), Ctx(exec, secret: ""));

        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.Empty(exec.Commands);
    }

    [Fact]
    public async Task NoChange_DoesNotRestartTheService()
    {
        var exec = new FakeNodeExec(c => c.Contains("is-active", StringComparison.Ordinal)
            ? new ExecResult(0, "active\n", "")
            : new ExecResult(0, "NOCHANGE\n", ""));

        var r = await new TracearrProvisioner().ApplyAsync(TracearrShape(), Ctx(exec));

        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("systemctl restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drift_WritesThenRestartsOnlyTracearr()
    {
        var exec = new FakeNodeExec(c =>
            c.Contains("python3 - check", StringComparison.Ordinal) ? new ExecResult(0, "CHANGED\n", "")
            : c.Contains("python3 - write", StringComparison.Ordinal) ? new ExecResult(0, "WROTE\n", "")
            : new ExecResult(0, "active\n", ""));

        var r = await new TracearrProvisioner().ApplyAsync(TracearrShape(), Ctx(exec));

        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        var restarts = exec.Commands.Where(c => c.Contains("systemctl restart", StringComparison.Ordinal)).ToList();
        Assert.Single(restarts);
        Assert.Contains("systemctl restart tracearr", restarts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceNotActiveAfterRestart_IsAFailureWithTheJournal()
    {
        var exec = new FakeNodeExec(c =>
            c.Contains("python3 - check", StringComparison.Ordinal) ? new ExecResult(0, "CHANGED\n", "")
            : c.Contains("python3 - write", StringComparison.Ordinal) ? new ExecResult(0, "WROTE\n", "")
            : c.Contains("is-active", StringComparison.Ordinal) ? new ExecResult(3, "failed\n", "")
            : new ExecResult(0, "journal-line\n", ""));

        var r = await new TracearrProvisioner().ApplyAsync(TracearrShape(), Ctx(exec));

        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.Contains("journal-line", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSecretNeverAppearsInPlanOrResultText()
    {
        var exec = new FakeNodeExec(c =>
            c.Contains("python3 - check", StringComparison.Ordinal) ? new ExecResult(0, "CHANGED\n", "")
            : c.Contains("python3 - write", StringComparison.Ordinal) ? new ExecResult(0, "WROTE\n", "")
            : new ExecResult(0, "active\n", ""));
        var shape = TracearrShape();

        var r = await new TracearrProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.DoesNotContain(Secret, r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new TracearrProvisioner().PlanSteps(shape), l => l.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderName_DefaultsToAuthentik()
    {
        Assert.Equal("authentik", TracearrProvisioner.ProviderName(TracearrShape()));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static (string Path, string Dir) WriteFixture()
    {
        var dir = Directory.CreateTempSubdirectory("tracearrtest").FullName;
        var path = Path.Combine(dir, ".env");
        File.WriteAllText(path, Fixture.ReplaceLineEndings("\n") + "\n");
        return (path, dir);
    }

    private static string RunMerger(string envPath, string mode, Dictionary<string, string>? desired = null) =>
        Run(envPath, mode, desired ?? Desired()).Stdout;

    private static (string Stdout, int Exit) Run(string envPath, string mode, Dictionary<string, string> desired)
    {
        // BuildMerger bakes in the real /data/tracearr/.env path; point it at the fixture so
        // the script under test is the shipped one, not a re-implementation of it.
        var script = TracearrProvisioner.BuildMerger(desired)
            .Replace($"\"{TracearrProvisioner.EnvFile}\"", $"\"{envPath}\"", StringComparison.Ordinal);

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

    private static string Capture(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
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
