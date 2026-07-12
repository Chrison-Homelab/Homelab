using PowerOrchestrator.Core.Config;
using Xunit;

namespace PowerOrchestrator.Tests;

public sealed class OrchestratorOptionsTests
{
    private static Func<string, string?> Env(Dictionary<string, string?> map) =>
        key => map.GetValueOrDefault(key);

    [Fact]
    public void Defaults_are_safe_dry_run()
    {
        var opts = OrchestratorOptions.FromEnvironment(Env([]));

        Assert.False(opts.Armed);                              // dry-run by default
        Assert.Equal(TimeSpan.FromSeconds(60), opts.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), opts.AwayDebounce);
        Assert.Equal("nuc-01", opts.SentinelNode);
        Assert.Equal(["desktop-01"], opts.ManagedNodes);
        Assert.Empty(opts.PresenceMacs);
        // Seeded registries mirror wake-node.sh.
        Assert.Equal("18:c0:4d:de:9f:82", opts.NodeMacs["desktop-01"]);
        Assert.Equal("192.168.179.2", opts.NodeAddresses["desktop-01"]);
    }

    [Fact]
    public void Env_overrides_are_parsed()
    {
        var opts = OrchestratorOptions.FromEnvironment(Env(new()
        {
            ["ORCH_ARMED"] = "true",
            ["ORCH_POLL_SECONDS"] = "30",
            ["ORCH_AWAY_DEBOUNCE_MINUTES"] = "5",
            ["ORCH_MANAGED_NODES"] = "desktop-01, hpe-02",
            ["ORCH_PRESENCE_MACS"] = "AA-BB-CC-DD-EE-FF, 11:22:33:44:55:66",
        }));

        Assert.True(opts.Armed);
        Assert.Equal(TimeSpan.FromSeconds(30), opts.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), opts.AwayDebounce);
        Assert.Equal(["desktop-01", "hpe-02"], opts.ManagedNodes);
        // Normalized to lowercase, colon-separated.
        Assert.Equal(["aa:bb:cc:dd:ee:ff", "11:22:33:44:55:66"], opts.PresenceMacs);
    }

    [Fact]
    public void NodeMacs_can_be_extended_via_env_merge()
    {
        var opts = OrchestratorOptions.FromEnvironment(Env(new()
        {
            ["ORCH_NODE_MACS"] = "hpe-02=de:ad:be:ef:00:01",
        }));

        Assert.Equal("de:ad:be:ef:00:01", opts.NodeMacs["hpe-02"]);
        Assert.Equal("18:c0:4d:de:9f:82", opts.NodeMacs["desktop-01"]); // defaults preserved
    }
}
