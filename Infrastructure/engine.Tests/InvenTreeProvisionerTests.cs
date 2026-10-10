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

    // ---- SSO (#485) -------------------------------------------------------

    private const string ClientId = "inventree";
    private const string ClientSecret = "s3cr3t-client-value-0123456789";
    private const string Issuer = "https://identity.chrison.dev/application/o/inventree/";

    private static Shape SsoShape()
    {
        var s = InvenTreeShape();
        s.Spec.Config["ssoIssuer"] = Issuer;
        return s;
    }

    private static InvenTreeProvisioner.SsoCredentials Creds(string secret = ClientSecret) => new(ClientId, secret);

    private static ConvergeContext SsoCtx(INodeExec exec, string? id = ClientId, string? secret = ClientSecret)
    {
        Environment.SetEnvironmentVariable(InvenTreeProvisioner.SsoClientIdKey, id);
        Environment.SetEnvironmentVariable(InvenTreeProvisioner.SsoClientSecretKey, secret);
        return Ctx(exec);
    }

    [Fact]
    public void Sso_IsOffUnlessTheShapeAsksForIt()
    {
        // Every shape that predates SSO must converge exactly as it did: no social keys, no
        // managed block, no API calls, and no extra plan lines.
        var script = InvenTreeProvisioner.BuildDeploy(InvenTreeShape(), "marker", Password);

        Assert.DoesNotContain("social_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("homelab-managed sso", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LOGIN_ENABLE_SSO", script, StringComparison.Ordinal);
        Assert.DoesNotContain(new InvenTreeProvisioner().PlanSteps(InvenTreeShape()), l => l.Contains("SSO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sso_Fails_WhenTheClientPairIsMissing()
    {
        // A provider with no credentials can never log anyone in, so this is a failed
        // provision rather than a quiet no-op.
        var exec = OkExec();

        var result = await new InvenTreeProvisioner().ApplyAsync(SsoShape(), SsoCtx(exec, id: null, secret: null));

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains(InvenTreeProvisioner.SsoClientIdKey, result.Message, StringComparison.Ordinal);
        Assert.Contains(InvenTreeProvisioner.SsoClientSecretKey, result.Message, StringComparison.Ordinal);
        Assert.Empty(exec.Commands);
    }

    [Fact]
    public async Task Sso_Fails_WhenOnlyHalfThePairIsSet()
    {
        var exec = OkExec();

        var result = await new InvenTreeProvisioner().ApplyAsync(SsoShape(), SsoCtx(exec, secret: null));

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Empty(exec.Commands);
    }

    [Fact]
    public async Task Sso_AppliesAndNeverLeaksTheClientSecret()
    {
        var exec = OkExec();
        var shape = SsoShape();

        var result = await new InvenTreeProvisioner().ApplyAsync(shape, SsoCtx(exec));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        // The secret belongs in the script that runs inside the CT and nowhere a human reads.
        Assert.DoesNotContain(ClientSecret, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new InvenTreeProvisioner().PlanSteps(shape), l => l.Contains(ClientSecret, StringComparison.Ordinal));
    }

    [Fact]
    public void Sso_WritesTheProviderBlockPointingAtTheDiscoveryDocument()
    {
        var shape = SsoShape();
        var script = InvenTreeProvisioner.BuildDeploy(shape, "marker", Password, Creds());
        var lines = InvenTreeProvisioner.SsoBlockLines(shape, Creds());

        // The YAML itself: the provider_id is the third path segment of the callback the
        // authentik blueprint registers, so it is load-bearing, not cosmetic.
        Assert.Contains("allauth.socialaccount.providers.openid_connect", lines[2], StringComparison.Ordinal);
        Assert.Contains("      - provider_id: 'authentik'", lines);
        Assert.Contains(
            "          server_url: 'https://identity.chrison.dev/application/o/inventree/.well-known/openid-configuration'",
            lines);

        // And how it reaches the file: every line is shell-quoted once more, so the YAML's own
        // single quotes appear as '\'' in the script text and the shell reassembles them.
        Assert.Contains(InvenTreeProvisioner.SsoBlockBegin, script, StringComparison.Ordinal);
        Assert.Contains("provider_id: '\\''authentik'\\''", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sso_ForcesTheConfigFileToOwnerOnly_BecauseItNowHoldsASecret()
    {
        // config.yaml ships 0664. The client secret has no `_file` variant, so it lands inline.
        var script = InvenTreeProvisioner.BuildDeploy(SsoShape(), "marker", Password, Creds());

        Assert.Contains($"chmod 600 {InvenTreeProvisioner.ConfigPath};", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sso_ValidatesTheConfigBeforeRestarting_AndRestoresTheBackupOnFailure()
    {
        // A block that breaks the YAML must not be the thing the restart trips over.
        var script = InvenTreeProvisioner.BuildDeploy(SsoShape(), "marker", Password, Creds());

        var backup = script.IndexOf($"cp -p {InvenTreeProvisioner.ConfigPath} ", StringComparison.Ordinal);
        var validate = script.IndexOf("yaml.safe_load", StringComparison.Ordinal);
        var restore = script.IndexOf("restored the backup, nothing restarted", StringComparison.Ordinal);
        var restart = script.IndexOf("systemctl restart", StringComparison.Ordinal);

        Assert.True(backup >= 0 && backup < validate, "backup must precede validation");
        Assert.True(validate < restore, "restore must follow a failed validation");
        Assert.True(restore < restart, "nothing may restart before validation has passed");
    }

    [Fact]
    public void Sso_RemovesItsOwnPreviousBlock_WithALiteralSedPattern()
    {
        // Regression: the pattern used to be Regex.Escape'd, which turns `(` into `\(` — a capture
        // group in sed's basic regex — so the old block was never matched and the NEXT converge
        // refused it as a foreign key. The pattern must be plain text.
        var script = InvenTreeProvisioner.BuildDeploy(SsoShape(), "marker", Password, Creds());

        Assert.Contains("sed -i '/^# >>> homelab-managed sso/,/^# <<< homelab-managed sso/d'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\\(", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sso_BlockMarkers_ContainNothingSedTreatsSpecially()
    {
        foreach (var marker in new[] { InvenTreeProvisioner.SsoBlockBeginPrefix, InvenTreeProvisioner.SsoBlockEnd })
            Assert.Empty(new[] { "\\", "[", "]", "*", ".", "^", "$" }.Where(c => marker.Contains(c, StringComparison.Ordinal)));
    }

    [Fact]
    public void Sso_RefusesAForeignSocialKey_RatherThanWritingADuplicate()
    {
        // A duplicate top-level YAML key is a parse error, which would take InvenTree down.
        var script = InvenTreeProvisioner.BuildDeploy(SsoShape(), "marker", Password, Creds());

        Assert.Contains("grep -qE '^(social_backends|social_providers):'", script, StringComparison.Ordinal);
        Assert.Contains("refusing to add a duplicate YAML key", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sso_EnablesTheDbSettings_AfterTheRestart_AndBeforeTheMarker()
    {
        var script = InvenTreeProvisioner.BuildDeploy(SsoShape(), "marker", Password, Creds());

        var restart = script.IndexOf("systemctl restart", StringComparison.Ordinal);
        var ssoOn = script.IndexOf("/api/settings/global/LOGIN_ENABLE_SSO/", StringComparison.Ordinal);
        var regOn = script.IndexOf("/api/settings/global/LOGIN_ENABLE_SSO_REG/", StringComparison.Ordinal);
        var marker = script.IndexOf(InvenTreeProvisioner.MarkerPath, StringComparison.Ordinal);

        Assert.True(restart < ssoOn && ssoOn < marker, "LOGIN_ENABLE_SSO is set after the restart, before the marker");
        Assert.True(restart < regOn && regOn < marker, "LOGIN_ENABLE_SSO_REG is set after the restart, before the marker");
        Assert.EndsWith($"printf '%s' 'marker' > {InvenTreeProvisioner.MarkerPath}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sso_KeepsTheAdminCredentialsOffCurlsArgv()
    {
        // `ps` inside the guest would otherwise show the superuser password for the length of the call.
        var script = InvenTreeProvisioner.BuildDeploy(SsoShape(), "marker", Password, Creds());

        Assert.Contains("curl -fsS -K -", script, StringComparison.Ordinal);
        Assert.DoesNotContain(" -u ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--user", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sso_DoublesSingleQuotesInYamlScalars()
    {
        var lines = InvenTreeProvisioner.SsoBlockLines(SsoShape(), Creds("a'b"));

        Assert.Contains("        secret: 'a''b'", lines);
    }

    [Fact]
    public void Sso_Marker_ChangesWhenTheClientSecretRotates()
    {
        // A rotated secret must re-converge or InvenTree keeps presenting the old one and every login fails.
        Assert.NotEqual(
            InvenTreeProvisioner.DesiredMarker(SsoShape(), Password, Creds()),
            InvenTreeProvisioner.DesiredMarker(SsoShape(), Password, Creds("rotated")));
    }

    [Fact]
    public void Sso_Marker_DiffersFromTheNoSsoMarker()
    {
        Assert.NotEqual(
            InvenTreeProvisioner.DesiredMarker(InvenTreeShape(), Password),
            InvenTreeProvisioner.DesiredMarker(SsoShape(), Password, Creds()));
    }

    [Fact]
    public async Task Sso_ReportsNoChange_WhenTheMarkerAlreadyMatches()
    {
        var shape = SsoShape();
        var exec = OkExec(markerReply: InvenTreeProvisioner.DesiredMarker(shape, Password, Creds()));

        var result = await new InvenTreeProvisioner().ApplyAsync(shape, SsoCtx(exec));

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        Assert.Single(exec.Commands);   // the marker read only: no rewrite, no restart
    }

    [Fact]
    public void Sso_RejectsAnIssuerThatIsNotHttps()
    {
        var s = InvenTreeShape();
        s.Spec.Config["ssoIssuer"] = "http://identity.chrison.dev/application/o/inventree/";

        Assert.Throws<InvalidOperationException>(() => InvenTreeProvisioner.SsoIssuer(s));
    }

    [Theory]
    [InlineData("auth/entik")]
    [InlineData("auth entik")]
    [InlineData("auth'entik")]
    public void Sso_RejectsAProviderIdThatIsNotAUrlSafeSegment(string id)
    {
        // It becomes a path segment in the callback URL and a value in YAML.
        var s = SsoShape();
        s.Spec.Config["ssoProviderId"] = id;

        Assert.Throws<InvalidOperationException>(() => InvenTreeProvisioner.SsoProviderId(s));
    }
}
