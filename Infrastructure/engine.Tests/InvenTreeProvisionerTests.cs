using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// InvenTreeProvisioner unit tests (#508). Pure + faked INodeExec — no live cluster.
//
// What is locked down here is the set of things that are silent when wrong: a password
// file with a trailing newline (the account exists but rejects the password in
// Bitwarden), a site_url still pointing at a DHCP address, and a missing secret being
// treated as "nothing to do" rather than a failed provision.
public sealed class InvenTreeProvisionerTests
{
    private const string Password = "correct-horse-battery-staple";

    private static Shape InvenTreeShape(string? siteUrl = "http://inventory.homelab.chrison.internal")
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "inventree", Stack = "Workshop" } };
        s.Spec.Node = "hpe-01";
        s.Spec.Ctid = "8000";
        s.Spec.App = "inventree";
        if (siteUrl is not null) s.Spec.Config["siteUrl"] = siteUrl;
        s.Spec.Config["adminUser"] = "admin";
        s.Spec.Config["adminEmail"] = "homelab@chrison.dev";
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

    private static FakeNodeExec OkExec(string markerReply = "") =>
        new(cmd => cmd.StartsWith("cat /etc/inventree/", StringComparison.Ordinal)
            ? new ExecResult(0, markerReply, "")
            : new ExecResult(0, "", ""));

    // SecretsEnv.Load(null) folds in the process environment, which is how the password
    // reaches the provisioner without a fixture file on disk.
    private static ConvergeContext Ctx(INodeExec exec, string? password = Password)
    {
        Environment.SetEnvironmentVariable(InvenTreeProvisioner.PasswordSecretKey, password);
        return new(exec, SecretsEnv.Load(null), new Dictionary<string, Shape>(), Deriver: null!);
    }

    // ---- registry wiring --------------------------------------------------

    [Fact]
    public void Registry_DispatchesInvenTreeProvisioner_ByApp()
    {
        Assert.IsType<InvenTreeProvisioner>(ProvisionerRegistry.Default().For("inventree"));
    }

    // ---- idempotency ------------------------------------------------------

    [Fact]
    public async Task ReportsNoChange_WhenMarkerAlreadyMatches()
    {
        var shape = InvenTreeShape();
        var exec = OkExec(markerReply: InvenTreeProvisioner.DesiredMarker(shape, Password));

        var result = await new InvenTreeProvisioner().ApplyAsync(shape, Ctx(exec));

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        // Only the marker read — no config rewrite, and crucially no service restart.
        Assert.Single(exec.Commands);
    }

    [Fact]
    public async Task Applies_WhenMarkerIsStale()
    {
        var exec = OkExec(markerReply: "stale-marker");

        var result = await new InvenTreeProvisioner().ApplyAsync(InvenTreeShape(), Ctx(exec));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
    }

    [Fact]
    public void Marker_ChangesWhenThePasswordChanges()
    {
        // A rotated password must re-converge, or the file on disk keeps the old value
        // while Bitwarden holds the new one.
        Assert.NotEqual(
            InvenTreeProvisioner.DesiredMarker(InvenTreeShape(), Password),
            InvenTreeProvisioner.DesiredMarker(InvenTreeShape(), "something-else"));
    }

    [Fact]
    public void Marker_ChangesWhenSiteUrlChanges()
    {
        Assert.NotEqual(
            InvenTreeProvisioner.DesiredMarker(InvenTreeShape(), Password),
            InvenTreeProvisioner.DesiredMarker(InvenTreeShape("http://10.10.0.99"), Password));
    }

    [Fact]
    public void Marker_IsStableForIdenticalInput()
    {
        Assert.Equal(
            InvenTreeProvisioner.DesiredMarker(InvenTreeShape(), Password),
            InvenTreeProvisioner.DesiredMarker(InvenTreeShape(), Password));
    }

    // ---- the guard --------------------------------------------------------

    [Fact]
    public async Task Fails_WhenTheAdminPasswordSecretIsMissing()
    {
        // Not NoChange and not Skipped: an InvenTree with no superuser cannot be logged
        // into and cannot mint an API token, so a converge that "succeeded" would be
        // reporting a usable guest that is not usable.
        var exec = OkExec();

        var result = await new InvenTreeProvisioner().ApplyAsync(InvenTreeShape(), Ctx(exec, password: null));

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains(InvenTreeProvisioner.PasswordSecretKey, result.Message, StringComparison.Ordinal);
        Assert.Empty(exec.Commands);
    }

    // ---- the recipe -------------------------------------------------------

    [Fact]
    public void Deploy_WritesThePasswordFileWithoutATrailingNewline()
    {
        // InvenTree's apps.py reads this file with read_text() and NO strip, so `echo`
        // would make "\n" part of the password. This assertion is the whole reason the
        // recipe uses printf.
        var script = InvenTreeProvisioner.BuildDeploy(InvenTreeShape(), "marker", Password);

        Assert.Contains($"printf '%s' '{Password}' > {InvenTreeProvisioner.PasswordFile}", script, StringComparison.Ordinal);
        Assert.DoesNotContain($"echo {Password}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_ChmodsThePasswordFileExplicitly()
    {
        // A umask only applies when the redirect CREATES the file, so it silently does
        // nothing on a re-converge over an existing one — which is how the first run left
        // the superuser password world-readable at 0644.
        var script = InvenTreeProvisioner.BuildDeploy(InvenTreeShape(), "marker", Password);

        Assert.Contains($"chmod 600 {InvenTreeProvisioner.PasswordFile}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("umask", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_PointsAdminPasswordFileAtTheFileItJustWrote()
    {
        var script = InvenTreeProvisioner.BuildDeploy(InvenTreeShape(), "marker", Password);

        // The quotes here are the SHELL's, not YAML's — the value lands in config.yaml as a
        // bare plain scalar, which is what the installer writes for site_url too. Nesting
        // YAML quotes inside shell quotes would emit '\''…'\'' and read back with the
        // quote characters as part of the path.
        Assert.Contains($"set_key admin_password_file '{InvenTreeProvisioner.PasswordFile}'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_SetsSiteUrlToTheDnsName_NotAnAddress()
    {
        var script = InvenTreeProvisioner.BuildDeploy(InvenTreeShape(), "marker", Password);

        Assert.Contains("set_key site_url 'http://inventory.homelab.chrison.internal'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_StampsTheMarkerLast()
    {
        // Mark-on-SUCCESS: a failure part way through must leave no current marker so the
        // next converge re-runs the whole recipe.
        var script = InvenTreeProvisioner.BuildDeploy(InvenTreeShape(), "marker", Password);

        Assert.EndsWith($"printf '%s' 'marker' > {InvenTreeProvisioner.MarkerPath}", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("systemctl restart", StringComparison.Ordinal) <
            script.IndexOf(InvenTreeProvisioner.MarkerPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Deploy_BailsWhenTheConfigFileIsAbsent()
    {
        // Appending keys to a config.yaml that does not exist would create a file
        // InvenTree never reads, and report success.
        var script = InvenTreeProvisioner.BuildDeploy(InvenTreeShape(), "marker", Password);

        Assert.Contains($"test -f {InvenTreeProvisioner.ConfigPath} ||", script, StringComparison.Ordinal);
    }
}
