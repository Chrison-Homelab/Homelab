using Microsoft.Extensions.Logging.Abstractions;
using PowerOrchestrator.Core.Config;
using PowerOrchestrator.Core.Idle;
using PowerOrchestrator.Core.Power;
using ProxmoxSharp;
using Xunit;

namespace PowerOrchestrator.Tests;

/// <summary>Safe behavior when Proxmox credentials are absent (review follow-ups on #217).</summary>
public sealed class DegradationTests
{
    [Fact]
    public async Task IdleProvider_without_creds_reports_offline_not_throws()
    {
        // Factory returns null (no creds) — must degrade, not throw every poll.
        var provider = new ProxmoxIdleProvider(() => null, NullLogger<ProxmoxIdleProvider>.Instance);

        var states = await provider.GetAsync(["desktop-01", "hpe-02"]);

        Assert.Equal(2, states.Count);
        Assert.All(states, s => Assert.False(s.IsOnline));
        Assert.All(states, s => Assert.Equal(0, s.RunningGuests));
    }

    [Fact]
    public async Task SleepAsync_without_creds_refuses_and_never_powers_off()
    {
        var options = OrchestratorOptions.FromEnvironment(_ => null);
        // pveFactory returns null → SleepAsync must throw BEFORE any SSH poweroff.
        var controller = new NodePowerController(
            options,
            pveFactory: () => (ProxmoxClientOptions?)null,
            ssh: new SshExec(),
            NullLogger<NodePowerController>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SleepAsync("desktop-01"));
    }
}
