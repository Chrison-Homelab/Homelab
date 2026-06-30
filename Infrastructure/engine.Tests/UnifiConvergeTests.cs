using Homelab.Infrastructure.Unifi;
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

    [Fact]
    public void Plan_creates_missing_keeps_present_add_only()
    {
        var declared = new[] { Pf("pangolin-https"), Pf("new-rule") };
        var plan = UnifiConverge.Plan(declared, existingNames: ["pangolin-https"]);

        Assert.Equal(["pangolin-https"], plan.AlreadyPresent);
        Assert.Equal(["new-rule"], plan.ToCreate.Select(p => p.Name));
    }

    [Fact]
    public void Plan_matches_names_case_insensitively()
    {
        var plan = UnifiConverge.Plan([Pf("Pangolin-HTTPS")], existingNames: ["pangolin-https"]);
        Assert.Empty(plan.ToCreate);
        Assert.Single(plan.AlreadyPresent);
    }

    [Fact]
    public void Plan_never_deletes_unmanaged_rules()
    {
        // Existing rules we didn't declare are simply ignored (add-only) — the plan
        // surfaces nothing about them.
        var plan = UnifiConverge.Plan([Pf("declared")], existingNames: ["hand-made-1", "hand-made-2"]);
        Assert.Equal(["declared"], plan.ToCreate.Select(p => p.Name));
        Assert.Empty(plan.AlreadyPresent);
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
