using Homelab.Infrastructure.Unifi;
using UnifiSharp.Legacy;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Pure tests for the UniFi network reconcile (converge-unifi). No controller —
// the planner and mapping are pure; live writes are covered by UnifiSharp's own
// container-backed tests.
public sealed class UnifiConvergeTests
{
    private static PortForwardSpec Pf(string name) => new()
    {
        Name = name, Enabled = true, Interface = "wan", Source = "any",
        DestinationPort = "443", ForwardIp = "10.10.0.13", ForwardPort = "443", Protocol = "tcp",
    };

    /// <summary>A live rule that matches <see cref="Pf"/> exactly.</summary>
    private static UnifiPortForward Live(string name, string fwd = "10.10.0.13", string dstPort = "443") => new()
    {
        Id = $"id-{name}", Name = name, Enabled = true, PfwdInterface = "wan", Src = "any",
        DstPort = dstPort, Fwd = fwd, FwdPort = "443", Proto = "tcp", Log = false,
    };

    [Fact]
    public void Plan_creates_missing_keeps_present_add_only()
    {
        var declared = new[] { Pf("pangolin-https"), Pf("new-rule") };
        var plan = UnifiConverge.Plan(declared, [Live("pangolin-https")]);

        Assert.Equal(["pangolin-https"], plan.AlreadyPresent);
        Assert.Equal(["new-rule"], plan.ToCreate.Select(p => p.Name));
        Assert.Empty(plan.ToUpdate);
    }

    [Fact]
    public void Plan_matches_names_case_insensitively()
    {
        var plan = UnifiConverge.Plan([Pf("Pangolin-HTTPS")], [Live("pangolin-https")]);
        Assert.Empty(plan.ToCreate);
        Assert.Single(plan.AlreadyPresent);
    }

    [Fact]
    public void Plan_never_deletes_unmanaged_rules()
    {
        // Existing rules we didn't declare are simply ignored (add-only) — the plan
        // surfaces nothing about them.
        var plan = UnifiConverge.Plan([Pf("declared")], [Live("hand-made-1"), Live("hand-made-2")]);
        Assert.Equal(["declared"], plan.ToCreate.Select(p => p.Name));
        Assert.Empty(plan.AlreadyPresent);
        Assert.Empty(plan.ToUpdate);
    }

    [Fact]
    public void Plan_detects_a_rule_that_drifted_instead_of_reporting_it_present()
    {
        // The bug this replaces: matching on name alone meant re-pointing the forward
        // target in the UI read as "present" and converge reported success forever.
        var plan = UnifiConverge.Plan([Pf("pangolin-https")], [Live("pangolin-https", fwd: "10.10.0.99")]);

        Assert.Empty(plan.AlreadyPresent);
        Assert.Empty(plan.ToCreate);
        var drift = Assert.Single(plan.ToUpdate);
        Assert.Equal("id-pangolin-https", drift.Id);
        Assert.Contains("fwd: 10.10.0.99 → 10.10.0.13", drift.Changes);
    }

    [Fact]
    public void Plan_reports_every_drifted_field_not_just_the_first()
    {
        var live = Live("pangolin-https", fwd: "10.10.0.99", dstPort: "8443") with { Proto = "udp" };
        var drift = Assert.Single(UnifiConverge.Plan([Pf("pangolin-https")], [live]).ToUpdate);

        Assert.Equal(3, drift.Changes.Count);
        Assert.Contains(drift.Changes, c => c.StartsWith("dst_port", StringComparison.Ordinal));
        Assert.Contains(drift.Changes, c => c.StartsWith("fwd:", StringComparison.Ordinal));
        Assert.Contains(drift.Changes, c => c.StartsWith("proto", StringComparison.Ordinal));
    }

    [Fact]
    public void Drift_ignores_fields_the_shape_does_not_declare()
    {
        // An undeclared field is not a claimed field — converge must not blank whatever
        // is set on the controller just because the shape is silent about it.
        var spec = new PortForwardSpec { Name = "n", DestinationPort = "443", ForwardIp = "10.10.0.13" };
        var live = new UnifiPortForward
        {
            Id = "x", Name = "n", DstPort = "443", Fwd = "10.10.0.13",
            Src = "192.168.1.0/24", Proto = "udp", FwdPort = "8443",
        };

        // Interface/Source/Protocol carry non-empty defaults on the spec, so only the
        // genuinely-unset ForwardPort is exempt here.
        Assert.DoesNotContain(UnifiConverge.Drift(live, spec), c => c.StartsWith("fwd_port", StringComparison.Ordinal));
    }

    [Fact]
    public void ToLegacy_maps_fields_to_the_api_shape()
    {
        var legacy = UnifiConverge.ToLegacy(Pf("pangolin-https"));
        Assert.Equal("pangolin-https", legacy.Name);
        Assert.Equal("wan", legacy.PfwdInterface);   // interface → pfwd_interface
        Assert.Equal("any", legacy.Src);
        Assert.Equal("443", legacy.DstPort);
        Assert.Equal("10.10.0.13", legacy.Fwd);
        Assert.Equal("443", legacy.FwdPort);
        Assert.Equal("tcp", legacy.Proto);
        Assert.True(legacy.Enabled);
    }

    [Fact]
    public void Load_parses_the_unifinetwork_document()
    {
        const string yaml =
            """
            apiVersion: homelab/v1
            kind: UnifiNetwork
            metadata:
              name: homelab-ingress
            spec:
              portForwards:
                - name: pangolin-https
                  enabled: true
                  interface: wan
                  source: any
                  destinationPort: "443"
                  forwardIp: 10.10.0.13
                  forwardPort: "443"
                  protocol: tcp
                  log: false
            """;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, yaml);
            var doc = UnifiConverge.Load(path);

            Assert.Equal("UnifiNetwork", doc.Kind);
            Assert.Equal("homelab-ingress", doc.Metadata.Name);
            var pf = Assert.Single(doc.Spec.PortForwards);
            Assert.Equal("pangolin-https", pf.Name);
            Assert.Equal("10.10.0.13", pf.ForwardIp);
            Assert.Equal("443", pf.DestinationPort);
        }
        finally { File.Delete(path); }
    }
}
