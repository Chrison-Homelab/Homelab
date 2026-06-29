using PowerOrchestrator.Core.Policy;

namespace PowerOrchestrator.Core.Model;

/// <summary>Per-node line in the status report.</summary>
public sealed record NodeReport(
    string Name,
    bool Online,
    int RunningGuests,
    bool Idle,
    string LastDecision,
    string LastReason);

/// <summary>
/// The orchestrator's current view of the world, served by <c>GET /status</c> and consumed by
/// the (PR2) web dashboard.
/// </summary>
public sealed record StatusReport(
    bool Armed,
    DateTimeOffset? LastPollUtc,
    int PresentCount,
    IReadOnlyList<string> PresentMacs,
    DateTimeOffset? AwaySince,
    IReadOnlyList<NodeReport> Nodes,
    IReadOnlyList<ArmPrecondition> ArmPreconditions);
