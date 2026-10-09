using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Dashboard;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// The dashboard is rendered from the shapes (ADR-0012, #47). These pin the two properties
// that make it trustworthy: nothing declared is dropped, and nothing exposed is hidden.
public sealed class HomepageDashboardTests
{
    private static ShapeLoader.LoadedStack StackOf(params Shape[] members) =>
        new(new StackShape { Metadata = new ShapeMetadata { Name = "Test" } }, members, Array.Empty<VmShape>());

    private static Shape Member(string name, string ctid = "5101", string? node = "hpe-01", params DashboardService[] services)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = name } };
        s.Spec.Ctid = ctid; s.Spec.Node = node;
        s.Metadata.Services.AddRange(services);
        return s;
    }

    private static Shape Pangolin(params (string name, string sub, string zone, bool sso)[] resources)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "pangolin" } };
        s.Spec.App = "debian"; s.Spec.Provisioner = "pangolin"; s.Spec.Ctid = "2013"; s.Spec.Node = "nuc-01";
        s.Spec.Config["baseDomain"] = "chrison.dev";
        s.Spec.Config["resources"] = resources.Select(r => (object)new Dictionary<object, object?>
        {
            ["name"] = r.name, ["subdomain"] = r.sub, ["zone"] = r.zone, ["sso"] = r.sso,
        }).ToList();
        return s;
    }

    [Fact]
    public void DeclaredService_RendersWithInternalHrefAndDerivedPublicRoute()
    {
        var sonarr = Member("sonarr", "5101", "hpe-01", new DashboardService
        {
            Name = "Sonarr", Url = "http://sonarr.homelab.chrison.internal:8989", Description = "TV",
            Widget = new() { ["type"] = "sonarr", ["keyFrom"] = "SONARR_API_KEY" },
        });
        var model = HomepageDashboard.Build(new[] { ("Media", StackOf(sonarr)), ("Core", StackOf(Pangolin(("Sonarr", "sonarr", "arr", true)))) });

        var e = Assert.Single(model.Entries);
        Assert.Equal("Media", e.Group);                                   // group defaults to the stack
        Assert.Equal("http://sonarr.homelab.chrison.internal:8989", e.Href);  // LAN link, never the public one
        Assert.Equal("sonarr", e.Icon);
        Assert.Contains("CT 5101 on hpe-01", e.Description);
        Assert.Contains("sonarr.arr.chrison.dev (Pangolin SSO)", e.Description);
        Assert.Empty(model.UnassignedRoutes);

        // The widget: keyFrom → key placeholder, url defaulted, secret recorded.
        Assert.NotNull(e.Widget);
        Assert.Equal("{{HOMEPAGE_VAR_SONARR_API_KEY}}", e.Widget!["key"]);
        Assert.False(e.Widget.ContainsKey("keyFrom"));
        Assert.Equal("http://sonarr.homelab.chrison.internal:8989", e.Widget["url"]);
        Assert.Contains("SONARR_API_KEY", model.SecretRefs);
    }

    [Fact]
    public void ExposedButUndeclared_IsRenderedAsAGapAndReported()
    {
        // The whole point of #47: a public UI missing from the dashboard must be visible, not absent.
        var model = HomepageDashboard.Build(new[] { ("Core", StackOf(Pangolin(("Radarr", "radarr", "arr", true), ("Seerr", "seerr", "arr", false)))) });

        Assert.Equal(2, model.UnassignedRoutes.Count);
        Assert.All(model.Entries, e => Assert.Equal(HomepageDashboard.UnassignedGroup, e.Group));
        Assert.Contains(model.Entries, e => e.Href == "https://seerr.arr.chrison.dev" && e.Description.Contains("app login only"));

        var problems = HomepageDashboard.Check(model, Array.Empty<string>());
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("radarr.arr.chrison.dev") && p.Contains("metadata.services"));
    }

    [Fact]
    public void NameMatching_IgnoresCaseParenthesesAndPunctuation()
    {
        // Pangolin names carry annotations ("Home Assistant (CT 6005)"); shapes use slugs.
        var ha = Member("homeassistant", "6005", "hpe-01", new DashboardService { Name = "Home Assistant", Url = "http://ha:8123" });
        var model = HomepageDashboard.Build(new[] { ("SmartHome", StackOf(ha)), ("Core", StackOf(Pangolin(("Home Assistant (CT 6005)", "homeassistant", "iot", true)))) });

        Assert.Empty(model.UnassignedRoutes);
        Assert.Contains("homeassistant.iot.chrison.dev (Pangolin SSO)", Assert.Single(model.Entries).Description);
    }

    [Fact]
    public void ExplicitPublic_OverridesDerivation_AndClaimsTheRoute()
    {
        // No route declared anywhere: shown verbatim.
        var svc = Member("x", "1", "n", new DashboardService { Name = "Thing", Url = "http://x", Public = "https://thing.example" });
        Assert.Contains("public https://thing.example", Assert.Single(HomepageDashboard.Build(new[] { ("S", StackOf(svc)) }).Entries).Description);

        // Authentik's public name is identity.chrison.dev — no name match possible, so the shape
        // says so explicitly, and that must claim the route (gate shown) instead of leaving it
        // reported as undeclared.
        var authentik = Member("authentik", "2014", "hpe-01", new DashboardService { Name = "Authentik", Url = "http://a:9000", Public = "https://identity.chrison.dev" });
        var tunnel = new Shape { Metadata = new ShapeMetadata { Name = "cloudflared" } };
        tunnel.Spec.App = "cloudflared"; tunnel.Spec.Ctid = "2010";
        tunnel.Spec.Config["ingress"] = new List<object> { new Dictionary<object, object?> { ["hostname"] = "identity.chrison.dev", ["service"] = "http://a:9000", ["public"] = true } };
        var model = HomepageDashboard.Build(new[] { ("Core", StackOf(authentik, tunnel)) });

        Assert.Empty(model.UnassignedRoutes);
        Assert.Contains("identity.chrison.dev (Cloudflare tunnel, no gate)", Assert.Single(model.Entries).Description);
    }

    [Fact]
    public void ShapeName_MatchesOnlyWhenTheShapeDeclaresASingleService()
    {
        // The pangolin CT declares Pangolin AND Traefik. Its own name must not pin
        // pangolin.chrison.dev onto the Traefik tile too.
        var pg = Pangolin(("Traefik Dashboard", "traefik", "lab", true));
        pg.Metadata.Services.Add(new DashboardService { Name = "Pangolin", Url = "http://p:3002" });
        pg.Metadata.Services.Add(new DashboardService { Name = "Traefik", Url = "http://p:8080" });
        var tunnel = new Shape { Metadata = new ShapeMetadata { Name = "cloudflared" } };
        tunnel.Spec.App = "cloudflared"; tunnel.Spec.Ctid = "2010";
        tunnel.Spec.Config["ingress"] = new List<object> { new Dictionary<object, object?> { ["hostname"] = "pangolin.chrison.dev", ["service"] = "http://p:3002" } };
        var model = HomepageDashboard.Build(new[] { ("Core", StackOf(pg, tunnel)) });

        var traefik = model.Entries.Single(e => e.Name == "Traefik");
        var pangolin = model.Entries.Single(e => e.Name == "Pangolin");
        Assert.Contains("traefik.lab.chrison.dev (Pangolin SSO)", traefik.Description);
        Assert.DoesNotContain("pangolin.chrison.dev", traefik.Description);
        Assert.Contains("pangolin.chrison.dev (Cloudflare Access)", pangolin.Description);
        Assert.Empty(model.UnassignedRoutes);    // the subdomain key matched; the display-name key must not re-report the host
    }

    [Fact]
    public void UndeclaredHost_IsReportedOnce_EvenWithTwoMatchKeys()
    {
        var model = HomepageDashboard.Build(new[] { ("Core", StackOf(Pangolin(("Power Orchestrator", "power", "lab", true)))) });
        Assert.Single(model.UnassignedRoutes);
        Assert.Single(model.Entries);
    }

    [Fact]
    public void Check_FlagsWidgetSecretsTheHostDoesNotExport()
    {
        var svc = Member("pulse-host", "4001", "hpe-01", new DashboardService
        {
            Name = "Pulse", Url = "http://m:7655", Widget = new() { ["type"] = "pulse", ["keyFrom"] = "PULSE_API_TOKEN" },
        });
        var model = HomepageDashboard.Build(new[] { ("Monitoring", StackOf(svc)) });

        Assert.NotEmpty(HomepageDashboard.Check(model, new[] { "HOMEPAGE_VAR_SONARR_API_KEY" }));
        Assert.Empty(HomepageDashboard.Check(model, new[] { "HOMEPAGE_VAR_PULSE_API_TOKEN" }));
    }

    [Fact]
    public void ExportedVars_ParsesQuadletSecretLines()
    {
        var quadlet = "[Container]\nSecret=pulse_api_token,type=env,target=HOMEPAGE_VAR_PULSE_API_TOKEN\nEnvironment=FOO=bar\n  Secret=x,type=env,target=HOMEPAGE_VAR_X\n";
        Assert.Equal(new[] { "HOMEPAGE_VAR_PULSE_API_TOKEN", "HOMEPAGE_VAR_X" }, HomepageDashboard.ExportedVars(quadlet));
    }

    [Fact]
    public void Render_IsHomepageShapedQuotedAndStable()
    {
        var svc = Member("grafana-host", "4001", "hpe-01", new DashboardService
        {
            Name = "Grafana", Url = "http://m:3000", Description = "Dashboards: all of them",
            Widget = new() { ["type"] = "grafana", ["version"] = "2", ["username"] = "admin", ["passwordFrom"] = "GF_SECURITY_ADMIN_PASSWORD" },
        });
        var model = HomepageDashboard.Build(new[] { ("Monitoring", StackOf(svc)) });
        var yaml = HomepageDashboard.Render(model);

        Assert.StartsWith("# GENERATED", yaml);
        Assert.Contains("- \"Monitoring\":\n    - \"Grafana\":\n        href: \"http://m:3000\"\n", yaml);
        Assert.Contains("description: \"Dashboards: all of them · CT 4001 on hpe-01\"", yaml);   // colon survives quoting
        Assert.Contains("siteMonitor: \"http://m:3000\"", yaml);
        Assert.Contains("          type: \"grafana\"\n          version: 2\n", yaml);              // numeric stays a number
        Assert.Contains("password: \"{{HOMEPAGE_VAR_GF_SECURITY_ADMIN_PASSWORD}}\"", yaml);   // braces quoted
        Assert.Equal(yaml, HomepageDashboard.Render(model));                                     // byte-stable
    }

    // ---- deploy (one-file push, not a converge) ----------------------------------

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

    private static Shape DashboardHost()
    {
        var h = new Shape { Metadata = new ShapeMetadata { Name = "podman-host" } };
        h.Spec.Node = "hpe-01"; h.Spec.Ctid = "4001";
        h.Spec.Config["user"] = "podman";
        h.Spec.Config["assetsTarget"] = "/home/podman/monitoring";
        h.Spec.Config["dashboard"] = new Dictionary<object, object?> { ["services"] = "homepage/services.yaml", ["unit"] = "homepage.service" };
        return h;
    }

    private static string Sha(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    [Fact]
    public async Task Deploy_UnchangedContent_WritesNothingAndDoesNotRestart()
    {
        const string yaml = "- \"A\":\n";
        var exec = new FakeExec(cmd => cmd.Contains("sha256sum") ? new ExecResult(0, Sha(yaml) + "\n", "") : new ExecResult(0, "", ""));
        var (msg, failed) = await DashboardCommand.DeployAsync(DashboardHost(), yaml, exec);

        Assert.Null(failed);
        Assert.Contains("no restart", msg);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("restart") || c.Contains("base64"));
    }

    [Fact]
    public async Task Deploy_ChangedContent_PushesToAssetsTargetAndRestartsOnlyTheDashboardUnit()
    {
        const string yaml = "- \"A\":\n";
        var exec = new FakeExec(cmd =>
            cmd.Contains("sha256sum") ? new ExecResult(0, "deadbeef\n", "")
            : cmd.Contains("LoadState") ? new ExecResult(0, "loaded\n", "")
            : new ExecResult(0, "", ""));
        var (msg, failed) = await DashboardCommand.DeployAsync(DashboardHost(), yaml, exec);

        Assert.Null(failed);
        Assert.Contains("homepage.service restarted", msg);
        Assert.Contains(exec.Commands, c => c.Contains("/home/podman/monitoring/homepage/services.yaml"));
        var restart = Assert.Single(exec.Commands, c => c.Contains("systemctl --user restart"));
        Assert.Contains("restart homepage.service", restart);
        Assert.DoesNotContain(exec.Commands, c => c.Contains("daemon-reload"));   // not a converge
    }

    [Fact]
    public async Task Deploy_UnitNotYetLoaded_StagesTheFileAndWarnsInsteadOfFailing()
    {
        // First merge after this lands: the workflow fires before the Monitoring host has been
        // converged with the quadlet. The file must still land; the run must say why nothing restarted.
        const string yaml = "- \"A\":\n";
        var exec = new FakeExec(cmd =>
            cmd.Contains("sha256sum") ? new ExecResult(0, "", "")
            : cmd.Contains("LoadState") ? new ExecResult(0, "not-found\n", "")
            : new ExecResult(0, "", ""));
        var (msg, failed) = await DashboardCommand.DeployAsync(DashboardHost(), yaml, exec);

        Assert.Null(failed);
        Assert.StartsWith("WARNING", msg);
        Assert.Contains("converge the Monitoring host", msg);
        Assert.Contains(exec.Commands, c => c.Contains("base64 -d"));               // pushed
        Assert.DoesNotContain(exec.Commands, c => c.Contains("systemctl --user restart"));
    }

    [Fact]
    public void Groups_AreOrderedWithUnassignedLast()
    {
        var a = Member("a", "1", "n", new DashboardService { Name = "Zed", Url = "http://z", Group = "Zeta" });
        var model = HomepageDashboard.Build(new[] { ("S", StackOf(a)), ("Core", StackOf(Pangolin(("Orphan", "orphan", "lab", true)))) });
        Assert.Equal(new[] { "Zeta", HomepageDashboard.UnassignedGroup }, model.Entries.Select(e => e.Group).Distinct());
    }
}
