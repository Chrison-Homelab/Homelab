using PowerOrchestrator.Core.Model;
using PowerOrchestrator.Core.Policy;
using Xunit;

namespace PowerOrchestrator.Tests;

public sealed class PowerPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);
    private static PresenceState Home => new(1, ["aa:bb:cc:dd:ee:ff"]);
    private static PresenceState Away => PresenceState.Empty;
    private static NodeState Online(int guests = 0) => new("desktop-01", IsOnline: true, RunningGuests: guests);
    private static NodeState Offline => new("desktop-01", IsOnline: false, RunningGuests: 0);

    [Fact]
    public void Present_and_offline_wakes()
    {
        var policy = new PowerPolicy(TimeSpan.FromMinutes(10));
        var d = policy.Evaluate(T0, Home, Offline);
        Assert.Equal(DecisionKind.Wake, d.Kind);
    }

    [Fact]
    public void Present_and_online_is_noop()
    {
        var policy = new PowerPolicy(TimeSpan.FromMinutes(10));
        var d = policy.Evaluate(T0, Home, Online());
        Assert.Equal(DecisionKind.NoOp, d.Kind);
    }

    [Fact]
    public void Away_and_idle_sleeps_only_after_debounce()
    {
        var policy = new PowerPolicy(TimeSpan.FromMinutes(10));

        // First observation starts the away timer — still within debounce → hold.
        var first = policy.Evaluate(T0, Away, Online());
        Assert.Equal(DecisionKind.NoOp, first.Kind);
        Assert.Equal(T0, policy.AwaySince);

        // Just before the window: still holding.
        var before = policy.Evaluate(T0.AddMinutes(9), Away, Online());
        Assert.Equal(DecisionKind.NoOp, before.Kind);

        // Past the window: sleep.
        var after = policy.Evaluate(T0.AddMinutes(11), Away, Online());
        Assert.Equal(DecisionKind.Sleep, after.Kind);
    }

    [Fact]
    public void Away_but_busy_never_sleeps()
    {
        var policy = new PowerPolicy(TimeSpan.FromMinutes(10));
        policy.Evaluate(T0, Away, Online(guests: 7));
        var d = policy.Evaluate(T0.AddHours(1), Away, Online(guests: 7));
        Assert.Equal(DecisionKind.NoOp, d.Kind);
    }

    [Fact]
    public void Away_and_already_offline_is_noop()
    {
        var policy = new PowerPolicy(TimeSpan.FromMinutes(10));
        var d = policy.Evaluate(T0.AddHours(1), Away, Offline);
        Assert.Equal(DecisionKind.NoOp, d.Kind);
    }

    [Fact]
    public void Returning_home_resets_the_away_timer()
    {
        var policy = new PowerPolicy(TimeSpan.FromMinutes(10));
        policy.Evaluate(T0, Away, Online());
        Assert.Equal(T0, policy.AwaySince);

        policy.Evaluate(T0.AddMinutes(5), Home, Online());
        Assert.Null(policy.AwaySince);

        // Away again starts a fresh debounce window from the new time.
        var d = policy.Evaluate(T0.AddMinutes(6), Away, Online());
        Assert.Equal(DecisionKind.NoOp, d.Kind);
        Assert.Equal(T0.AddMinutes(6), policy.AwaySince);
    }
}
