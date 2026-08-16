using Homelab.Infrastructure.Unifi;
using UnifiSharp.Legacy;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Pure tests for the controller-local DNS reconcile (#314/#419). No controller —
// everything that decides whether to write is a function of (declared, live).
public sealed class UnifiStaticDnsTests
{
    private static StaticDnsSpec Spec(string name = "*.lab.chrison.dev", string value = "10.10.0.13",
                                      string type = "A", int? ttl = null, bool enabled = true) =>
        new() { Name = name, Value = value, Type = type, Ttl = ttl, Enabled = enabled };

    private static UnifiStaticDnsRecord Live(string key = "*.lab.chrison.dev", string value = "10.10.0.13",
                                             string type = "A", int ttl = 300, bool enabled = true) =>
        new() { Id = "live-1", Key = key, Value = value, RecordType = type, Ttl = ttl, Enabled = enabled };

    // ---- the reconcile decision ----

    [Fact]
    public void Matching_live_state_is_a_no_op()
    {
        var item = UnifiStaticDns.Plan(Spec(), [Live()]);
        Assert.Equal(StaticDnsAction.NoChange, item.Action);
        Assert.Empty(item.Changes);
    }

    [Fact]
    public void A_missing_record_is_a_create()
    {
        var item = UnifiStaticDns.Plan(Spec(), [Live("*.arr.chrison.dev")]);
        Assert.Equal(StaticDnsAction.Create, item.Action);
        Assert.Null(item.LiveId);
    }

    [Fact]
    public void A_record_answering_differently_is_drift_not_a_second_record()
    {
        // A name can only answer one way, so "already exists" is not the question —
        // "does it answer what the shape says" is. This is the case create-if-missing
        // would call healthy while the LAN resolved to the wrong host.
        var item = UnifiStaticDns.Plan(Spec(value: "10.10.0.13"), [Live(value: "118.67.199.127")]);

        Assert.Equal(StaticDnsAction.Update, item.Action);
        Assert.Equal("live-1", item.LiveId);
        Assert.Contains("value: 118.67.199.127 → 10.10.0.13", item.Changes);
    }

    [Fact]
    public void A_disabled_record_is_drift()
    {
        var item = UnifiStaticDns.Plan(Spec(), [Live(enabled: false)]);
        Assert.Contains("enabled: False → True", item.Changes);
    }

    [Fact]
    public void The_same_name_with_a_different_type_is_a_different_record()
    {
        // An A and a CNAME for one name are distinct rows on the controller; matching on
        // name alone would make converge try to turn one into the other.
        var item = UnifiStaticDns.Plan(Spec(type: "A"), [Live(type: "CNAME", value: "elsewhere.example")]);
        Assert.Equal(StaticDnsAction.Create, item.Action);
    }

    // ---- what the shape does and does not claim ----

    [Fact]
    public void Ttl_is_only_claimed_when_the_shape_states_one()
    {
        // Otherwise every converge would rewrite the controller's default forever.
        Assert.Equal(StaticDnsAction.NoChange, UnifiStaticDns.Plan(Spec(ttl: null), [Live(ttl: 900)]).Action);
        Assert.Contains("ttl: 900 → 60", UnifiStaticDns.Plan(Spec(ttl: 60), [Live(ttl: 900)]).Changes);
    }

    [Fact]
    public void Cosmetic_differences_are_not_drift()
    {
        // A trailing dot and case are the same name; a zero-padded octet is the same address.
        Assert.Equal(StaticDnsAction.NoChange,
            UnifiStaticDns.Plan(Spec(name: "*.lab.chrison.dev"), [Live(key: "*.LAB.Chrison.Dev.")]).Action);
        Assert.Equal(StaticDnsAction.NoChange,
            UnifiStaticDns.Plan(Spec(value: "10.10.0.13"), [Live(value: "10.10.000.13")]).Action);
    }

    [Fact]
    public void A_cname_value_compares_as_a_name_not_an_address()
    {
        var item = UnifiStaticDns.Plan(
            Spec(type: "CNAME", value: "traefik.lab.chrison.dev"),
            [Live(type: "CNAME", value: "Traefik.Lab.Chrison.Dev.")]);
        Assert.Equal(StaticDnsAction.NoChange, item.Action);
    }

    // ---- the write body ----

    [Fact]
    public void ToRecord_sends_a_complete_record_because_v2_rejects_partials()
    {
        var r = UnifiStaticDns.ToRecord(Spec());

        Assert.Equal("*.lab.chrison.dev", r.Key);
        Assert.Equal("A", r.RecordType);
        Assert.Equal("10.10.0.13", r.Value);
        Assert.Equal(300, r.Ttl);        // controller default, sent explicitly
        Assert.True(r.Enabled);
        Assert.Null(r.Id);               // create carries no id
    }

    [Fact]
    public void An_update_carries_the_live_records_untouched_fields_forward()
    {
        // A full replacement that rebuilt from defaults would silently reset fields the
        // shape says nothing about — the exact hazard of a replace-only endpoint.
        var live = Live(ttl: 900) with { Priority = 7, Weight = 3, Port = 8443 };
        var r = UnifiStaticDns.ToRecord(Spec(value: "10.10.0.99"), live);

        Assert.Equal("live-1", r.Id);
        Assert.Equal("10.10.0.99", r.Value);
        Assert.Equal(900, r.Ttl);        // not reset to 300
        Assert.Equal(7, r.Priority);
        Assert.Equal(3, r.Weight);
        Assert.Equal(8443, r.Port);
    }

    [Fact]
    public void A_stated_ttl_wins_over_the_live_one()
    {
        Assert.Equal(60, UnifiStaticDns.ToRecord(Spec(ttl: 60), Live(ttl: 900)).Ttl);
    }

    // ---- add-only ----

    [Fact]
    public void Undeclared_records_are_listed_and_never_touched()
    {
        // The controller carries a dozen records owned by other things — the Azure Lab's
        // wildcard set, the node names. Reporting them is the whole contract.
        var live = new[]
        {
            Live("*.lab.chrison.dev"),
            Live("*.topaz.local.dev", "10.50.0.10"),
            Live("hpe-01.homelab.chrison.internal", "10.0.0.13"),
        };

        var undeclared = UnifiStaticDns.Undeclared(live, [Spec("*.lab.chrison.dev")]);

        Assert.Equal(["*.topaz.local.dev", "hpe-01.homelab.chrison.internal"], undeclared.Select(u => u.Key));
    }

    [Fact]
    public void Undeclared_matching_ignores_case_and_trailing_dots()
    {
        var live = new[] { Live("*.LAB.chrison.dev.") };
        Assert.Empty(UnifiStaticDns.Undeclared(live, [Spec("*.lab.chrison.dev")]));
    }

    // ---- the actual #419 declaration ----

    [Fact]
    public void The_three_pangolin_zones_plan_as_creates_against_a_controller_that_has_none()
    {
        var declared = new[]
        {
            Spec("*.lab.chrison.dev"), Spec("*.arr.chrison.dev"), Spec("*.iot.chrison.dev"),
        };
        var live = new[] { Live("*.topaz.local.dev", "10.50.0.10") };

        var plan = declared.Select(d => UnifiStaticDns.Plan(d, live)).ToList();

        Assert.All(plan, p => Assert.Equal(StaticDnsAction.Create, p.Action));
        Assert.All(plan, p => Assert.Equal("10.10.0.13", p.Desired.Value));
    }
}
