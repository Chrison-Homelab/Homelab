using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// `${VAR}` expansion in spec.config — the indirection that keeps the home WAN IPv4 and
// the DHCPv6-PD prefix out of a PUBLIC repo (and out of its public Actions logs) while
// leaving the keys themselves declared and reviewable in the shape.
public sealed class ShapeVarsTests
{
    private static SecretsEnv Vars(params (string Key, string Value)[] pairs)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"secrets-{Guid.NewGuid():N}.env");
        File.WriteAllLines(tmp, pairs.Select(p => $"{p.Key}={p.Value}"));
        try { return SecretsEnv.Load(tmp); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Expands_a_scalar()
    {
        var config = new Dictionary<string, object?> { ["publicIp"] = "${HOME_WAN_IP}" };
        ShapeVars.Expand(config, Vars(("HOME_WAN_IP", "203.0.113.7")), "test.lxc.yaml");
        Assert.Equal("203.0.113.7", config["publicIp"]);
    }

    [Fact]
    public void Expands_inside_nested_maps_and_lists()
    {
        // The shape that motivated this: cloudflared's access.bypass is a LIST nested two
        // maps deep, and YamlDotNet hands those back as Dictionary<object,object>/List<object>
        // rather than the typed dictionary at the top — so a scalar-only walk would miss it.
        var bypass = new List<object?> { "${HOME_WAN_IP}/32", "${HOME_WAN_IPV6_PREFIX}/56" };
        var access = new Dictionary<object, object?> { ["bypass"] = bypass };
        var config = new Dictionary<string, object?> { ["access"] = access };

        ShapeVars.Expand(config, Vars(("HOME_WAN_IP", "203.0.113.7"),
                                      ("HOME_WAN_IPV6_PREFIX", "2001:db8:116d:e500::")), "test.lxc.yaml");

        Assert.Equal(new object?[] { "203.0.113.7/32", "2001:db8:116d:e500::/56" }, bypass);
    }

    [Fact]
    public void An_unset_variable_is_fatal_and_names_itself()
    {
        // The whole point. Both consumers fail INVISIBLY on an empty value — an absent
        // publicIp reports the DNS step as "skipped", and a dropped bypass entry silently
        // re-arms Cloudflare Access's one-time PIN across the admin surface. Neither may
        // ever be reached by substituting nothing.
        var config = new Dictionary<string, object?> { ["publicIp"] = "${HOME_WAN_IP}" };
        var ex = Assert.Throws<InvalidOperationException>(
            () => ShapeVars.Expand(config, Vars(("SOMETHING_ELSE", "x")), "core/pangolin.lxc.yaml"));

        Assert.Contains("HOME_WAN_IP", ex.Message);
        Assert.Contains("core/pangolin.lxc.yaml", ex.Message);
    }

    [Fact]
    public void A_key_present_but_blank_counts_as_unset()
    {
        // secrets-sync.sh leaves a key it could not find in Secrets Manager BLANK rather
        // than absent, so "present" is not the test — a half-filled secrets.env must fail
        // exactly like an empty one.
        var config = new Dictionary<string, object?> { ["publicIp"] = "${HOME_WAN_IP}" };
        Assert.Throws<InvalidOperationException>(
            () => ShapeVars.Expand(config, Vars(("HOME_WAN_IP", "")), "test.lxc.yaml"));
    }

    [Fact]
    public void Leaves_ordinary_values_and_non_strings_alone()
    {
        var config = new Dictionary<string, object?>
        {
            ["gerbilEndpoint"] = "10.10.0.13",
            ["leStaging"] = false,
            ["port"] = 443,
            ["nothing"] = null,
        };
        var changed = ShapeVars.Expand(config, Vars(("HOME_WAN_IP", "203.0.113.7")), "test.lxc.yaml");

        Assert.Equal(0, changed);
        Assert.Equal("10.10.0.13", config["gerbilEndpoint"]);
        Assert.Equal(false, config["leStaging"]);
        Assert.Equal(443, config["port"]);
        Assert.Null(config["nothing"]);
    }

    [Fact]
    public void A_dollar_sign_that_is_not_a_reference_is_not_touched()
    {
        // Passwords and shell snippets live in config too; only ${NAME} is a reference.
        var config = new Dictionary<string, object?> { ["cmd"] = "echo $HOME and $ and ${}" };
        var changed = ShapeVars.Expand(config, Vars(("HOME", "/root")), "test.lxc.yaml");

        Assert.Equal(0, changed);
        Assert.Equal("echo $HOME and $ and ${}", config["cmd"]);
    }
}
