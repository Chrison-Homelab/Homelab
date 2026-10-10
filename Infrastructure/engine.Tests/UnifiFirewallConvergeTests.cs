using System.Text.Json.Nodes;
using Homelab.Infrastructure.Unifi;
using UnifiSharp.Firewall;
using Xunit;

namespace Homelab.Infrastructure.Tests;

public class UnifiFirewallConvergeTests
{
    private static readonly Guid External = Guid.NewGuid(), Homelab = Guid.NewGuid();
    private static readonly FirewallNames Names = new(
        new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["External"] = External, ["Homelab"] = Homelab },
        new Dictionary<string, Guid>());

    private static FirewallPolicySpec Qbit(string? iid = "::6342:9") => new()
    {
        Name = "qbittorrent-peers-v6",
        Source = new() { Zone = "External" },
        Destination = new() { Zone = "Homelab", Ipv6InterfaceId = iid, Ports = ["63429"] },
        IpVersion = FirewallIpVersion.IPv6,
        Protocol = "tcp_udp",
    };

    private static LiveFirewallPolicy Live(FirewallPolicySpec s, string origin = "USER_DEFINED")
    {
        var raw = FirewallPolicyJson.ToJson(s, Names);
        raw["metadata"] = new JsonObject { ["origin"] = origin };
        return new(Guid.NewGuid(), s.Name, origin, raw);
    }

    [Fact]
    public void Missing_policy_is_created()
    {
        var r = UnifiFirewallConverge.Plan([Qbit()], [], Names);
        Assert.Equal(FirewallPolicyAction.Create, Assert.Single(r.Items).Action);
    }

    [Fact]
    public void Matching_policy_is_left_alone()
    {
        var r = UnifiFirewallConverge.Plan([Qbit()], [Live(Qbit())], Names);
        Assert.Equal(FirewallPolicyAction.NoChange, Assert.Single(r.Items).Action);
    }

    [Fact]
    public void A_pinned_address_policy_is_replaced_by_the_interface_id_one_with_a_readable_diff()
    {
        // The hand-made 2026-10-07 policy pinned the full address; the declared one uses the IID.
        var handMade = Qbit(iid: null) with { Destination = new() { Zone = "Homelab", Addresses = ["2407:8b00:116d:e502::6342:9"], Ports = ["63429"] } };
        var item = Assert.Single(UnifiFirewallConverge.Plan([Qbit()], [Live(handMade)], Names).Items);
        Assert.Equal(FirewallPolicyAction.Update, item.Action);
        Assert.Contains(item.Changes, c => c.StartsWith("destination.addresses:"));
        Assert.Contains(item.Changes, c => c.StartsWith("destination.ipv6InterfaceId: - → ::6342:9"));
    }

    [Fact]
    public void A_system_defined_policy_with_the_same_name_is_never_overwritten()
    {
        var r = UnifiFirewallConverge.Plan([Qbit()], [Live(Qbit(), "SYSTEM_DEFINED")], Names);
        Assert.Empty(r.Items);
        Assert.Contains("left alone", Assert.Single(r.Conflicts));
    }

    [Fact]
    public void Duplicate_user_policies_are_a_conflict_not_a_guess()
    {
        var r = UnifiFirewallConverge.Plan([Qbit()], [Live(Qbit()), Live(Qbit())], Names);
        Assert.Empty(r.Items);
        Assert.Single(r.Conflicts);
    }

    [Fact]
    public void Undeclared_user_policies_are_listed_and_system_ones_ignored()
    {
        var other = Qbit() with { Name = "Internal → Azure" };
        var sys = Qbit() with { Name = "Block All Traffic" };
        var r = UnifiFirewallConverge.Plan([], [Live(other), Live(sys, "SYSTEM_DEFINED")], Names);
        Assert.Equal(["Internal → Azure"], r.Undeclared);
    }

    [Fact]
    public void Network_yaml_declares_the_qbittorrent_policy_by_full_address()
    {
        // By ADDRESS, not ipv6InterfaceId: the UCG stores an IID match but never matches it
        // (live 2026-10-09, #687). If this ever flips back to an IID, re-check the hit counter.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Infrastructure", "unifi", "network.yaml"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var doc = UnifiConverge.Load(Path.Combine(dir!, "Infrastructure", "unifi", "network.yaml"));
        var spec = Assert.Single(doc.Spec.FirewallPolicies, p => p.Name == "qbittorrent-peers-v6").ToSpec();
        Assert.Null(spec.Destination.Ipv6InterfaceId);
        Assert.Single(spec.Destination.Addresses, a => a.EndsWith("::6342:9"));
        Assert.Equal(FirewallIpVersion.IPv6, spec.IpVersion);
        // Must round-trip through the API mapping without losing anything.
        Assert.Equal(spec, FirewallPolicyJson.FromJson(FirewallPolicyJson.ToJson(spec, Names), Names).Spec);
    }

    [Fact]
    public void Decl_rejects_unknown_action_and_ip_version()
    {
        Assert.Throws<InvalidOperationException>(() => new FirewallPolicyDecl { Name = "x", Action = "drop" }.ToSpec());
        Assert.Throws<InvalidOperationException>(() => new FirewallPolicyDecl { Name = "x", IpVersion = "v6" }.ToSpec());
    }
}
