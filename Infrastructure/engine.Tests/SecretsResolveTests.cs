using System.Net;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

public sealed class SecretsResolveTests
{
    private static string Temp(string name, string content)
    {
        var dir = Directory.CreateTempSubdirectory("secrets-resolve-").FullName;
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static (Dictionary<string, string>?, string?) Bao(params (string K, string V)[] kv) =>
        (kv.ToDictionary(x => x.K, x => x.V, StringComparer.Ordinal), null);

    [Fact]
    public void OpenBao_WinsOverAStaleSecretsEnv()
    {
        // The failure this exists for: a checkout's secrets.env is old, OpenBao is current.
        var file = Temp("secrets.env", "ZZ_RESOLVE_TOKEN='stale'\nZZ_RESOLVE_ONLY_IN_FILE='x'\n");
        var env = SecretsEnv.Resolve(file, null, () => Bao(("ZZ_RESOLVE_TOKEN", "fresh")), ci: false);
        Assert.Equal("fresh", env.Get("ZZ_RESOLVE_TOKEN"));
        // With OpenBao up, secrets.env is not consulted at all, so a key only it has is absent.
        Assert.Null(env.Get("ZZ_RESOLVE_ONLY_IN_FILE"));
        Assert.StartsWith("OpenBao (1)", env.Source);
    }

    [Fact]
    public void SecretsEnv_IsTheFallback_WhenOpenBaoIsUnavailable_AndSaysWhy()
    {
        var file = Temp("secrets.env", "ZZ_RESOLVE_FB='from-file'\n");
        var env = SecretsEnv.Resolve(file, null, () => (null, "SEALED — unseal on CT 3007"), ci: false);
        Assert.Equal("from-file", env.Get("ZZ_RESOLVE_FB"));
        Assert.Contains("⚠ OpenBao not used (SEALED", env.Source);
    }

    [Fact]
    public void Ci_NeverContactsOpenBao_AndUsesTheJobEnvironment()
    {
        var called = false;
        var env = SecretsEnv.Resolve(null, null, () => { called = true; return (null, "x"); }, ci: true);
        Assert.False(called);
        Assert.Contains("process env", env.Source);
        Assert.DoesNotContain("⚠", env.Source);
    }

    [Fact]
    public void TemplateLiterals_FillWhatNothingElseSet_AndNeverOverride()
    {
        var tmpl = Temp("secrets.env.template", "ZZ_RESOLVE_LIT=literal\nZZ_RESOLVE_TOKEN=\nZZ_RESOLVE_BOTH=from-template\n");
        var env = SecretsEnv.Resolve(null, tmpl, () => Bao(("ZZ_RESOLVE_BOTH", "from-bao")), ci: false);
        Assert.Equal("literal", env.Get("ZZ_RESOLVE_LIT"));
        Assert.Equal("from-bao", env.Get("ZZ_RESOLVE_BOTH"));
        Assert.Null(env.Get("ZZ_RESOLVE_TOKEN"));            // a fill target is not a literal
    }

    [Theory]
    [InlineData("UNIFI_LOCAL_HOST=192.168.178.1   # IP on purpose — see the note above", "192.168.178.1")]
    [InlineData("A='quoted value # not a comment'  # comment", "quoted value # not a comment")]
    [InlineData("A=\"dq\"", "dq")]
    [InlineData("export A=x", "x")]
    [InlineData("A=x\t# tab comment", "x")]
    public void TemplateLiterals_AreReadTheWayTheShellReadsThem(string line, string expected)
    {
        var (_, v) = Assert.Single(SecretsEnv.TemplateLiterals(new[] { line }));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("A=")]
    [InlineData("# A=x")]
    [InlineData("A='unbalanced")]
    [InlineData("1BAD=x")]
    public void TemplateLiterals_SkipWhatIsNotAValue(string line) =>
        Assert.Empty(SecretsEnv.TemplateLiterals(new[] { line }));

    [Fact]
    public void ExportConnectionSettings_OnlyWhitelisted_AndNeverOverridesTheShell()
    {
        const string mine = "NODE_ADDR_ZZRESOLVE_A", preset = "NODE_ADDR_ZZRESOLVE_B", other = "ZZ_RESOLVE_NOT_EXPORTED";
        Environment.SetEnvironmentVariable(preset, "from-shell");
        try
        {
            var env = SecretsEnv.Resolve(null, null, () => Bao((mine, "a"), (preset, "from-bao"), (other, "secret")), ci: false);
            env.ExportConnectionSettings();
            Assert.Equal("a", Environment.GetEnvironmentVariable(mine));
            Assert.Equal("from-shell", Environment.GetEnvironmentVariable(preset));
            Assert.Null(Environment.GetEnvironmentVariable(other));   // secrets stay out of the process env
        }
        finally
        {
            Environment.SetEnvironmentVariable(mine, null);
            Environment.SetEnvironmentVariable(preset, null);
        }
    }

    // ── OpenBaoSource against a scripted HTTP handler ──
    private sealed class Script : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _f;
        public Script(Func<HttpRequestMessage, (HttpStatusCode, string)> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add($"{r.Method} {r.RequestUri!.AbsolutePath}");
            var (code, body) = _f(r);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    private static OpenBaoSource Src(Script h, string? role = "r", string? secret = "s") =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://bao.test/") },
            k => k.EndsWith("role-id") ? role : secret);

    [Fact]
    public async Task OpenBao_Sealed_IsAReason_AndNoLoginIsAttempted()
    {
        var h = new Script(_ => (HttpStatusCode.OK, "{\"initialized\":true,\"sealed\":true}"));
        var (map, why) = await Src(h).LoadAsync(CancellationToken.None);
        Assert.Null(map);
        Assert.Contains("SEALED", why);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task OpenBao_LogsIn_ReadsEveryKey_AndRevokesItsOwnToken()
    {
        var h = new Script(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/sys/seal-status" => (HttpStatusCode.OK, "{\"initialized\":true,\"sealed\":false}"),
            "/v1/auth/approle/login" => (HttpStatusCode.OK, "{\"auth\":{\"client_token\":\"t\"}}"),
            "/v1/secret/metadata/homelab" => (HttpStatusCode.OK, "{\"data\":{\"keys\":[\"A\",\"B\",\"sub/\"]}}"),
            "/v1/secret/data/homelab/A" => (HttpStatusCode.OK, "{\"data\":{\"data\":{\"value\":\"1\"}}}"),
            "/v1/secret/data/homelab/B" => (HttpStatusCode.OK, "{\"data\":{\"data\":{\"value\":\"2\"}}}"),
            _ => (HttpStatusCode.OK, "{}"),
        });
        var (map, why) = await Src(h).LoadAsync(CancellationToken.None);
        Assert.Null(why);
        Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, map);
        Assert.Contains("POST /v1/auth/token/revoke-self", h.Calls);
    }

    [Fact]
    public async Task OpenBao_NoAppRole_IsAReason()
    {
        var h = new Script(_ => (HttpStatusCode.OK, "{\"initialized\":true,\"sealed\":false}"));
        var (map, why) = await Src(h, role: null).LoadAsync(CancellationToken.None);
        Assert.Null(map);
        Assert.Contains("no AppRole", why);
    }
}
