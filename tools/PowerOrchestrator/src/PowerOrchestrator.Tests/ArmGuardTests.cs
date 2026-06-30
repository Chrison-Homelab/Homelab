using PowerOrchestrator.Core.Policy;
using Xunit;

namespace PowerOrchestrator.Tests;

public sealed class ArmGuardTests
{
    [Fact]
    public void Cannot_arm_while_191_blockers_unmet()
    {
        var canArm = ArmGuard.CanArm(out var unmet);

        Assert.False(canArm);
        Assert.NotEmpty(unmet);
        // The two #191 prerequisites are surfaced with actionable detail.
        Assert.Contains(unmet, p => p.Name == "services-evacuated");
        Assert.Contains(unmet, p => p.Name == "qdevice-quorum");
        Assert.All(unmet, p => Assert.False(p.Met));
        Assert.All(unmet, p => Assert.False(string.IsNullOrWhiteSpace(p.Detail)));
    }
}
