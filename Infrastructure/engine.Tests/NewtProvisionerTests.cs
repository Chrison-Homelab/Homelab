using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// NewtProvisioner unit tests (#441). Pure + faked INodeExec — no live cluster.
// The things worth pinning are the ones that are irreversible or silent: never clobbering
// credentials Pangolin will not reissue, never deleting a site to recover, and the root-binary
// posture that Pangolin SSH mode requires.
public sealed class NewtProvisionerTests
{
    private static Shape NewtShape()
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "newt", Stack = "DevOps" } };
        s.Spec.Node = "desktop-01";
        s.Spec.Ctid = "3005";
        s.Spec.App = "debian";
        s.Spec.Provisioner = "newt";
        s.Spec.Config["siteName"] = "DevOps";
        s.Spec.Config["pangolinUrl"] = "https://pangolin.chrison.dev";
        s.Spec.Config["version"] = "1.16.0";
        return s;
    }

    private sealed class FakeNodeExec : INodeExec
    {
        private readonly Func<string, ExecResult> _reply;
        public List<string> Commands { get; } = new();
        public FakeNodeExec(Func<string, ExecResult> reply) => _reply = reply;

        public Task<ExecResult> OnNodeAsync(string node, string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(_reply(command));
        }

        public Task<ExecResult> InContainerAsync(string node, string ctid, string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(_reply(command));
        }
    }

    private static ConvergeContext Ctx(INodeExec exec) =>
        new(exec, SecretsEnv.Load(null), new Dictionary<string, Shape>(), Deriver: null!);

    // ---- registry + catalogue --------------------------------------------

    [Fact]
    public void Registry_DispatchesNewtProvisioner()
    {
        Assert.IsType<NewtProvisioner>(ProvisionerRegistry.Default().For("newt"));
    }

    // ---- the unit --------------------------------------------------------

    [Fact]
    public void Unit_RunsTheBinaryAsRoot()
    {
        // Not laziness: Pangolin SSH mode requires Newt as root on the connector host, and
        // upstream does not support the containerized form at all. A future "tidy this into a
        // quadlet" change would break SSH resources.
        var unit = NewtProvisioner.BuildUnit(NewtShape());

        Assert.Contains("User=root", unit, StringComparison.Ordinal);
        Assert.Contains($"ExecStart={NewtProvisioner.BinPath}", unit, StringComparison.Ordinal);
        Assert.Contains($"EnvironmentFile={NewtProvisioner.EnvPath}", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=always", unit, StringComparison.Ordinal);
        Assert.Contains("DevOps", unit, StringComparison.Ordinal);
    }

    // ---- the deploy script ----------------------------------------------

    [Fact]
    public void Deploy_FetchesThePinnedBinaryAtomically()
    {
        var script = NewtProvisioner.BuildDeploy(NewtShape(), ("id1", "sec1"), "m", "/p");

        Assert.Contains("releases/download/1.16.0/newt_linux_amd64", script, StringComparison.Ordinal);
        // -f, so an HTTP error page never lands as the binary; and staged via .new + mv so a
        // truncated download never becomes /usr/local/bin/newt.
        Assert.Contains("curl -fsSL", script, StringComparison.Ordinal);
        Assert.Contains($"mv {NewtProvisioner.BinPath}.new {NewtProvisioner.BinPath}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_WritesCredentialsAt0600_WhenItHasThem()
    {
        var script = NewtProvisioner.BuildDeploy(NewtShape(), ("id1", "sec1"), "m", "/p");

        Assert.Contains("NEWT_ID=id1", script, StringComparison.Ordinal);
        Assert.Contains("NEWT_SECRET=sec1", script, StringComparison.Ordinal);
        Assert.Contains("PANGOLIN_ENDPOINT=https://pangolin.chrison.dev", script, StringComparison.Ordinal);
        Assert.Contains($"chmod 0600 {NewtProvisioner.EnvPath}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_DoesNotTouchTheEnvFile_WhenItHasNoCredentials()
    {
        // The irreversible one. Pangolin issues newtId/secret ONCE at site-create and offers no
        // way to read them back, so a re-converge that rewrote the env file with blanks would
        // strand the site permanently.
        var script = NewtProvisioner.BuildDeploy(NewtShape(), null, "m", "/p");

        Assert.DoesNotContain($"cat > {NewtProvisioner.EnvPath}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NEWT_SECRET", script, StringComparison.Ordinal);
        // ...but the binary and unit are still reconciled, so a version bump works.
        Assert.Contains("newt_linux_amd64", script, StringComparison.Ordinal);
        Assert.Contains($"systemctl restart {NewtProvisioner.UnitName}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_StampsTheMarkerLast()
    {
        var script = NewtProvisioner.BuildDeploy(NewtShape(), ("id1", "sec1"), "abc123", "/etc/newt/.homelab-managed");

        var stamp = script.IndexOf("printf '%s' 'abc123'", StringComparison.Ordinal);
        Assert.True(stamp > 0, "the marker must be stamped");
        foreach (var step in new[] { "apt-get install", "newt_linux_amd64", "systemctl restart newt.service" })
        {
            var at = script.IndexOf(step, StringComparison.Ordinal);
            Assert.True(at > 0, $"expected the deploy script to contain '{step}'");
            Assert.True(stamp > at, $"the marker must be stamped after '{step}'");
        }
    }

    // ---- the marker ------------------------------------------------------

    [Fact]
    public void Marker_ExcludesCredentials_ButTracksVersionAndSite()
    {
        // Credentials are not desired state — they are issued once and then fixed. Hashing them
        // would tie the marker to a value we cannot re-read, so a converge that could not
        // resolve them would present as permanent drift.
        var a = NewtShape();
        var b = NewtShape();
        Assert.Equal(NewtProvisioner.DesiredMarker(a), NewtProvisioner.DesiredMarker(b));

        b.Spec.Config["version"] = "1.17.0";
        Assert.NotEqual(NewtProvisioner.DesiredMarker(a), NewtProvisioner.DesiredMarker(b));

        var c = NewtShape();
        c.Spec.Config["siteName"] = "Media";
        Assert.NotEqual(NewtProvisioner.DesiredMarker(a), NewtProvisioner.DesiredMarker(c));
    }

    // ---- idempotency -----------------------------------------------------

    [Fact]
    public async Task ReportsNoChange_OnlyWhenTheMarkerMatchesAndTheEnvFileExists()
    {
        var shape = NewtShape();
        var marker = NewtProvisioner.DesiredMarker(shape);
        var exec = new FakeNodeExec(cmd =>
            cmd.StartsWith("cat /etc/newt/.homelab-managed", StringComparison.Ordinal) ? new ExecResult(0, marker, "")
            : cmd.Contains("test -s /etc/newt/newt.env", StringComparison.Ordinal) ? new ExecResult(0, "yes", "")
            : new ExecResult(0, "", ""));

        var result = await new NewtProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
    }

    [Fact]
    public async Task DoesNotReportNoChange_WhenTheMarkerMatchesButCredentialsAreGone()
    {
        // A rebuilt CT under an unchanged shape: the marker would match, but there is no
        // connector configured. Reporting NoChange there would leave the site permanently down
        // and converge permanently satisfied.
        var shape = NewtShape();
        var marker = NewtProvisioner.DesiredMarker(shape);
        var exec = new FakeNodeExec(cmd =>
            cmd.StartsWith("cat /etc/newt/.homelab-managed", StringComparison.Ordinal) ? new ExecResult(0, marker, "")
            : new ExecResult(0, "", ""));   // env file absent

        var result = await new NewtProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.NotEqual(ApplyOutcome.NoChange, result.Outcome);
    }

    // ---- accessors -------------------------------------------------------

    [Fact]
    public void SiteName_DefaultsToTheStack()
    {
        // The per-stack model (#442): a site per stack, named after it.
        var s = NewtShape();
        s.Spec.Config.Remove("siteName");

        Assert.Equal("DevOps", NewtProvisioner.SiteName(s));
    }

    [Fact]
    public void Packages_IncludeCurlAndCaCertificates()
    {
        // A bare Debian CT has neither, and both are needed to fetch the release over TLS.
        var packages = NewtProvisioner.Packages(NewtShape());

        Assert.Contains("curl", packages);
        Assert.Contains("ca-certificates", packages);
    }
}
