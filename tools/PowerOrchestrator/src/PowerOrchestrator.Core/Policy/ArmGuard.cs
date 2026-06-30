namespace PowerOrchestrator.Core.Policy;

/// <summary>A prerequisite that must hold before automatic sleep can be safely armed.</summary>
public sealed record ArmPrecondition(string Name, bool Met, string Detail);

/// <summary>
/// Gate for arming automatic sleep. The #191 blockers are encoded here as preconditions: until
/// desktop-01's always-on services are evacuated and cluster quorum is hardened with a QDevice,
/// automatic sleep must stay disarmed (a wrong auto-sleep would drop the tunnel/git/CI or break
/// quorum). When these flip to Met, <c>POST /policy/arm</c> stops returning 409.
/// <para>These are deliberately hard-coded for PR1 (no live probe yet). Replace with real checks
/// when the blockers are addressed.</para>
/// </summary>
public static class ArmGuard
{
    public static IReadOnlyList<ArmPrecondition> Preconditions() =>
    [
        new("services-evacuated", Met: false,
            "desktop-01 still hosts always-on services (cloudflared, forgejo + runners, ERP, topaz); " +
            "evacuate them (e.g. to hpe-02) before it can ever auto-sleep — issue #191."),
        new("qdevice-quorum", Met: false,
            "no QDevice configured; auto-sleeping a heavy node could drop the cluster below quorum — issue #191."),
    ];

    /// <summary>True only when every precondition is met. Otherwise <paramref name="unmet"/> lists the gaps.</summary>
    public static bool CanArm(out IReadOnlyList<ArmPrecondition> unmet)
    {
        unmet = Preconditions().Where(p => !p.Met).ToList();
        return unmet.Count == 0;
    }
}
