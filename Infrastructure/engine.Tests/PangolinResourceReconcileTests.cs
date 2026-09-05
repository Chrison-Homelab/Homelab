using System.Text.Json;
using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Pangolin resource reconciliation (issue #309). The provisioner used to be add-only:
// find-or-create by fullDomain, and an existing resource was skipped ENTIRELY, target
// included. Editing a target in a shape therefore updated desired state and never reached
// Pangolin — converge reported "0 drifted, 3 up-to-date" while pulse.lab.chrison.dev
// pointed at a container that had just been stopped.
//
// These drive the real ApplyAsync with a fake exec standing in for `curl` inside the CT,
// so the assertions are about the HTTP calls actually issued.
public sealed class PangolinResourceReconcileTests : IDisposable
{
    private readonly string _secrets = Path.Combine(Path.GetTempPath(), $"hl309-{Guid.NewGuid():n}.env");

    public PangolinResourceReconcileTests() =>
        // A file, not process env: SecretsEnv folds the environment in, and mutating that
        // from a test leaks into every other test in the run.
        File.WriteAllText(_secrets, "PANGOLIN_API_KEY=test-key\n");

    public void Dispose()
    {
        try { File.Delete(_secrets); } catch { /* best-effort */ }
    }

    // ---- fixtures ---------------------------------------------------------

    // One declared resource: traefik.lab.chrison.dev → http://localhost:8080.
    private static Shape Shape(string ip = "localhost", int port = 8080, string method = "http", bool? sso = null,
        IEnumerable<Dictionary<string, object?>>? rules = null)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "pangolin" } };
        s.Spec.Ctid = "2013";
        s.Spec.Node = "nuc-01";
        s.Spec.Config["dashboardUrl"] = "https://pangolin.chrison.dev";
        s.Spec.Config["baseDomain"] = "chrison.dev";
        s.Spec.Config["edge"] = "cloudflared";          // ssl=false; keeps Cloudflare out of it
        s.Spec.Config["org"] = "chrison-dev";
        var res = new Dictionary<string, object?>
        {
            ["name"] = "Traefik Dashboard",
            ["subdomain"] = "traefik",
            ["zone"] = "lab",
            ["target"] = new Dictionary<string, object?> { ["ip"] = ip, ["port"] = port, ["method"] = method },
        };
        if (sso is not null) res["sso"] = sso.Value;
        if (rules is not null) res["rules"] = rules.Cast<object>().ToList();
        s.Spec.Config["resources"] = new List<object> { res };
        return s;
    }

    // Live resource list. `sso` is deliberately an INTEGER — that is what Pangolin
    // actually returns (SQLite-backed), and GetBoolean() throws on it.
    private static string ResourcesJson(int targetCount = 1, object? sso = null, bool ssl = false) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                resources = new[]
                {
                    new
                    {
                        resourceId = 1,
                        fullDomain = "traefik.lab.chrison.dev",
                        ssl,
                        sso = sso ?? 1,
                        targets = Enumerable.Range(1, targetCount)
                            .Select(i => new { targetId = i, ip = "localhost", port = 8080, enabled = true }).ToArray(),
                    },
                },
            },
            success = true,
        });

    private static string TargetsJson(string ip = "localhost", int port = 8080, string method = "http") =>
        JsonSerializer.Serialize(new
        {
            data = new { targets = new[] { new { targetId = 1, ip, port, method, enabled = true, siteId = 1 } } },
            success = true,
        });

    // The Ruddarr shape: bypass auth on the API, keep the UI behind SSO.
    private static Dictionary<string, object?>[] ApiBypassRules(string action = "ACCEPT") => new[]
    {
        new Dictionary<string, object?> { ["action"] = action, ["match"] = "PATH", ["value"] = "/api/*" },
        new Dictionary<string, object?> { ["action"] = action, ["match"] = "PATH", ["value"] = "/ping" },
    };

    private static string RulesJson(params (int id, string action, string match, string value, int priority, bool enabled)[] rules) =>
        JsonSerializer.Serialize(new
        {
            data = new { rules = rules.Select(r => new { ruleId = r.id, r.action, r.match, r.value, r.priority, r.enabled }).ToArray() },
            success = true,
        });

    // GET /resource/{id} — the only place applyRules is visible (the embedded list omits it).
    // Like sso, it comes back SQLite-shaped (0/1) as often as not.
    private static string DetailJson(object applyRules) =>
        JsonSerializer.Serialize(new { data = new { resourceId = 1, applyRules }, success = true });

    private sealed class FakeExec : INodeExec
    {
        private readonly Func<string, ExecResult> _reply;
        public List<string> Commands { get; } = new();
        public FakeExec(Func<string, ExecResult> reply) => _reply = reply;
        public Task<ExecResult> OnNodeAsync(string node, string cmd, CancellationToken ct = default)
        { Commands.Add(cmd); return Task.FromResult(_reply(cmd)); }
        public Task<ExecResult> InContainerAsync(string node, string ctid, string cmd, CancellationToken ct = default)
        { Commands.Add(cmd); return Task.FromResult(_reply(cmd)); }
    }

    // Standard happy-path API surface; `resources` and `targets` are the interesting knobs.
    private FakeExec Api(string resources, string targets, Func<string, ExecResult>? extra = null,
        string? rules = null, string? detail = null)
    {
        var marker = PangolinProvisioner.DesiredMarker(Shape());
        return new FakeExec(cmd =>
        {
            var custom = extra?.Invoke(cmd);
            if (custom is not null) return custom;
            if (cmd.Contains("homelab-managed")) return new ExecResult(0, $"# homelab-managed: {marker}", "");
            if (cmd.Contains("/v1/org/chrison-dev/domains")) return new ExecResult(0,
                """{"data":{"domains":[{"domainId":"domain1","baseDomain":"chrison.dev"}]},"success":true}""", "");
            if (cmd.Contains("/v1/org/chrison-dev/sites")) return new ExecResult(0,
                """{"data":{"sites":[{"siteId":1,"type":"local"}]},"success":true}""", "");
            if (cmd.Contains("/v1/org/chrison-dev/resources")) return new ExecResult(0, resources, "");
            if (cmd.Contains("/v1/resource/1/targets")) return new ExecResult(0, targets, "");
            if (cmd.Contains("/v1/resource/1/rules") && !IsWrite(cmd)) return new ExecResult(0, rules ?? RulesJson(), "");
            if (cmd.TrimEnd().EndsWith("/v1/resource/1", StringComparison.Ordinal) && !IsWrite(cmd))
                return new ExecResult(0, detail ?? DetailJson(0), "");
            return new ExecResult(0, """{"success":true,"data":{}}""", "");
        });
    }

    private async Task<(ApplyResult Result, FakeExec Exec)> RunAsync(Shape shape, FakeExec exec)
    {
        var ctx = new ConvergeContext(exec, SecretsEnv.Load(_secrets), new Dictionary<string, Shape>(), Deriver: null!);
        var result = await new PangolinProvisioner().ApplyAsync(shape, ctx);
        return (result, exec);
    }

    private static bool IsWrite(string cmd) =>
        cmd.Contains("-X POST", StringComparison.Ordinal) || cmd.Contains("-X PUT", StringComparison.Ordinal);

    // ---- in sync ----------------------------------------------------------

    [Fact]
    public async Task InSync_WritesNothing()
    {
        var (result, exec) = await RunAsync(Shape(), Api(ResourcesJson(), TargetsJson()));

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        Assert.Contains("0 retargeted", result.Message);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("/v1/target/", StringComparison.Ordinal) && IsWrite(c));
    }

    [Fact]
    public async Task IntegerSso_IsNotMistakenForDrift()
    {
        // Pangolin returns sso as 1, the shape's default is `true`. Reading that with
        // GetBoolean() throws, and treating a non-bool as false would re-gate all 15
        // resources on every single run.
        var (result, exec) = await RunAsync(Shape(), Api(ResourcesJson(sso: 1), TargetsJson()));

        Assert.Contains("0 re-gated", result.Message);
        Assert.DoesNotContain(exec.Commands, c => IsGateWrite(c));
    }

    // A gate write is POST /v1/resource/{id} — NOT /v1/resource/{id}/target, which is the
    // target-create call and would otherwise match a naive substring check.
    private static bool IsGateWrite(string cmd) =>
        IsWrite(cmd) && cmd.TrimEnd().EndsWith("/v1/resource/1", StringComparison.Ordinal);

    // ---- target drift -----------------------------------------------------

    [Fact]
    public async Task IpDrift_IssuesTargetUpdateWithSiteId()
    {
        // The #309 scenario: shape moved to a new host, live still points at the old one.
        var (result, exec) = await RunAsync(Shape(ip: "10.10.0.41"), Api(ResourcesJson(), TargetsJson(ip: "10.10.0.40")));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains("1 retargeted", result.Message);
        Assert.Contains("10.10.0.40", result.Message);   // reports before → after
        Assert.Contains("10.10.0.41", result.Message);

        var write = Assert.Single(exec.Commands, c => c.Contains("/v1/target/1", StringComparison.Ordinal) && IsWrite(c));
        Assert.Contains("\"ip\":\"10.10.0.41\"", write);
        // siteId is REQUIRED even when unchanged — omitting it 400s.
        Assert.Contains("\"siteId\":1", write);
    }

    [Fact]
    public async Task PortDrift_IssuesTargetUpdate()
    {
        var (result, _) = await RunAsync(Shape(port: 8081), Api(ResourcesJson(), TargetsJson(port: 8080)));
        Assert.Contains("1 retargeted", result.Message);
    }

    [Fact]
    public async Task MethodDrift_IsDetected()
    {
        // `method` is absent from the embedded target list on GET resources, so catching
        // this is the whole reason for the extra GET /resource/{id}/targets call.
        var (result, _) = await RunAsync(Shape(method: "https"), Api(ResourcesJson(), TargetsJson(method: "http")));
        Assert.Contains("1 retargeted", result.Message);
    }

    [Fact]
    public async Task DisabledTarget_IsReEnabled()
    {
        var targets = JsonSerializer.Serialize(new
        {
            data = new { targets = new[] { new { targetId = 1, ip = "localhost", port = 8080, method = "http", enabled = false, siteId = 1 } } },
            success = true,
        });
        var (result, exec) = await RunAsync(Shape(), Api(ResourcesJson(), targets));

        Assert.Contains("1 retargeted", result.Message);
        var write = Assert.Single(exec.Commands, c => c.Contains("/v1/target/1", StringComparison.Ordinal) && IsWrite(c));
        Assert.Contains("\"enabled\":true", write);
    }

    // ---- gates ------------------------------------------------------------

    [Fact]
    public async Task SsoDisabledLive_IsReGated()
    {
        // Security-relevant (#238): a resource created before sso defaulted ON, or flipped
        // off by hand in the UI, must not stay open forever.
        var (result, exec) = await RunAsync(Shape(), Api(ResourcesJson(sso: 0), TargetsJson()));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains("1 re-gated", result.Message);
        Assert.Contains(exec.Commands, c => IsGateWrite(c) && c.Contains("\"sso\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitSsoFalse_IsHonouredAndNotFoughtEveryRun()
    {
        // Native clients (Plex, audiobookshelf) opt out. Live already 0 → no write.
        var (result, exec) = await RunAsync(Shape(sso: false), Api(ResourcesJson(sso: 0), TargetsJson()));

        Assert.Contains("0 re-gated", result.Message);
        Assert.DoesNotContain(exec.Commands, c => IsGateWrite(c));
    }

    // ---- access rules (API bypass for native clients) -----------------------

    private static bool IsRuleCreate(string cmd) =>
        IsWrite(cmd) && cmd.TrimEnd().EndsWith("/v1/resource/1/rule", StringComparison.Ordinal);
    private static bool IsRuleUpdate(string cmd) =>
        IsWrite(cmd) && cmd.Contains("/v1/resource/1/rule/", StringComparison.Ordinal);

    [Fact]
    public async Task NoRulesDeclared_TouchesNeitherRulesNorApplyRules()
    {
        // The dozen SSO-only resources must behave exactly as before: no rule reads, no
        // applyRules write. (An applyRules write is a gate write — POST /v1/resource/1.)
        var (_, exec) = await RunAsync(Shape(), Api(ResourcesJson(), TargetsJson()));

        Assert.DoesNotContain(exec.Commands, c => c.Contains("/rule", StringComparison.Ordinal));
        Assert.DoesNotContain(exec.Commands, c => IsGateWrite(c));
    }

    [Fact]
    public async Task DeclaredRules_AreCreatedThenApplyRulesIsSwitchedOn()
    {
        var (result, exec) = await RunAsync(Shape(rules: ApiBypassRules()), Api(ResourcesJson(), TargetsJson()));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains("1 rule set(s) changed", result.Message);

        var creates = exec.Commands.Where(IsRuleCreate).ToList();
        Assert.Equal(2, creates.Count);
        Assert.Contains(creates, c => c.Contains("\"action\":\"ACCEPT\"") && c.Contains("\"match\":\"PATH\"") && c.Contains("\"value\":\"/api/*\"") && c.Contains("\"priority\":1"));
        Assert.Contains(creates, c => c.Contains("\"value\":\"/ping\"") && c.Contains("\"priority\":2") && c.Contains("\"enabled\":true"));

        // Rules are inert until applyRules is on — and it must be turned on AFTER they exist.
        var apply = Assert.Single(exec.Commands, c => IsGateWrite(c) && c.Contains("applyRules", StringComparison.Ordinal));
        Assert.Contains("\"applyRules\":true", apply);
        Assert.True(exec.Commands.IndexOf(apply) > exec.Commands.FindLastIndex(IsRuleCreate));
        // …and nothing about the sso gate itself was touched.
        Assert.DoesNotContain(exec.Commands, c => IsGateWrite(c) && c.Contains("\"sso\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RulesInSync_WriteNothing()
    {
        var live = RulesJson((10, "ACCEPT", "PATH", "/api/*", 1, true), (11, "ACCEPT", "PATH", "/ping", 2, true));
        var (result, exec) = await RunAsync(Shape(rules: ApiBypassRules()),
            Api(ResourcesJson(), TargetsJson(), rules: live, detail: DetailJson(1)));

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        Assert.Contains("0 rule set(s) changed", result.Message);
        Assert.DoesNotContain(exec.Commands, c => IsRuleCreate(c) || IsRuleUpdate(c) || IsGateWrite(c));
    }

    [Fact]
    public async Task RuleActionDrift_IsUpdatedInPlace()
    {
        // Someone flipped the bypass to DROP in the UI: the API path is now blocked for Ruddarr.
        var live = RulesJson((10, "DROP", "PATH", "/api/*", 1, true), (11, "ACCEPT", "PATH", "/ping", 2, true));
        var (result, exec) = await RunAsync(Shape(rules: ApiBypassRules()),
            Api(ResourcesJson(), TargetsJson(), rules: live, detail: DetailJson(1)));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var update = Assert.Single(exec.Commands, IsRuleUpdate);
        Assert.Contains("/v1/resource/1/rule/10", update);
        Assert.Contains("\"action\":\"ACCEPT\"", update);
        Assert.DoesNotContain(exec.Commands, IsRuleCreate);
        Assert.Contains("DROP/p1/on → ACCEPT/p1/on", result.Message);
    }

    [Fact]
    public async Task ApplyRulesOffLive_IsSwitchedBackOn()
    {
        // Rules present but the layer disabled by hand — reads as "in sync" on the rules
        // alone, yet Ruddarr gets the SSO interstitial. applyRules is part of desired state.
        var live = RulesJson((10, "ACCEPT", "PATH", "/api/*", 1, true), (11, "ACCEPT", "PATH", "/ping", 2, true));
        var (result, exec) = await RunAsync(Shape(rules: ApiBypassRules()),
            Api(ResourcesJson(), TargetsJson(), rules: live, detail: DetailJson(false)));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains("rules enabled", result.Message);
        Assert.Single(exec.Commands, c => IsGateWrite(c) && c.Contains("\"applyRules\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UndeclaredLiveRule_IsLeftAloneAndReported()
    {
        // Same add-only stance as resources: a hand-added rule is neither deleted nor
        // silently tolerated — it is named in the summary so it can be declared or removed.
        var live = RulesJson((10, "ACCEPT", "PATH", "/api/*", 1, true), (11, "ACCEPT", "PATH", "/ping", 2, true),
            (12, "DROP", "COUNTRY", "RU", 3, true));
        var (result, exec) = await RunAsync(Shape(rules: ApiBypassRules()),
            Api(ResourcesJson(), TargetsJson(), rules: live, detail: DetailJson(1)));

        Assert.Equal(ApplyOutcome.NoChange, result.Outcome);
        Assert.Contains("undeclared live rule DROP COUNTRY RU", result.Message);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("-X DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedRuleAction_FailsTheApply()
    {
        // "bypass" reads naturally but is not what Pangolin calls it; guessing would either
        // 400 at the API or, worse, land as something else. Fail loudly.
        var (result, exec) = await RunAsync(Shape(rules: ApiBypassRules(action: "bypass")), Api(ResourcesJson(), TargetsJson()));

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("not ACCEPT|DROP|PASS", result.Message);
        Assert.DoesNotContain(exec.Commands, c => IsRuleCreate(c) || IsGateWrite(c));
    }

    [Fact]
    public async Task RuleListUnreachable_FailsInsteadOfCreatingDuplicates()
    {
        var exec = Api(ResourcesJson(), TargetsJson(),
            extra: cmd => cmd.Contains("/v1/resource/1/rules") ? new ExecResult(1, "", "connection refused") : null);
        var (result, _) = await RunAsync(Shape(rules: ApiBypassRules()), exec);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("GET rules failed", result.Message);
        Assert.DoesNotContain(exec.Commands, IsRuleCreate);
    }

    // ---- guardrails -------------------------------------------------------

    [Fact]
    public async Task MultipleTargets_AreLeftAlone()
    {
        // Pangolin supports several targets per resource for load balancing; our shapes
        // declare one. Rewriting the first of several would destroy a hand-built config,
        // which the add-only guardrail in CLAUDE.md exists to prevent.
        var (result, exec) = await RunAsync(Shape(ip: "10.10.0.41"), Api(ResourcesJson(targetCount: 3), TargetsJson()));

        Assert.Contains("left alone", result.Message);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("/v1/target/", StringComparison.Ordinal) && IsWrite(c));
    }

    [Fact]
    public async Task ZeroTargets_GetsOneAdded()
    {
        var (result, exec) = await RunAsync(Shape(), Api(ResourcesJson(targetCount: 0), TargetsJson()));

        Assert.Contains("target added", result.Message);
        Assert.Contains(exec.Commands, c => c.Contains("/v1/resource/1/target", StringComparison.Ordinal) && IsWrite(c));
    }

    [Fact]
    public async Task ResourceListUnreachable_FailsInsteadOfReportingSuccess()
    {
        // Reporting a clean run when the API could not be read is the exact failure mode
        // #309 is about. A read error must not look like "nothing to do".
        var exec = Api(ResourcesJson(), TargetsJson(),
            extra: cmd => cmd.Contains("/v1/org/chrison-dev/resources") ? new ExecResult(1, "", "connection refused") : null);
        var (result, _) = await RunAsync(Shape(), exec);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("cannot reconcile safely", result.Message);
    }
}
