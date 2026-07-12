namespace PowerOrchestrator.Core.Model;

/// <summary>What the policy decided this poll cycle.</summary>
public enum DecisionKind
{
    /// <summary>Do nothing.</summary>
    NoOp,
    /// <summary>The node should be woken (WoL).</summary>
    Wake,
    /// <summary>The node should be put to sleep (guests stopped + host poweroff).</summary>
    Sleep,
}

/// <summary>A policy decision plus the human-readable reason it was reached.</summary>
public sealed record Decision(DecisionKind Kind, string Reason)
{
    public static Decision NoOp(string reason) => new(DecisionKind.NoOp, reason);
    public static Decision Wake(string reason) => new(DecisionKind.Wake, reason);
    public static Decision Sleep(string reason) => new(DecisionKind.Sleep, reason);
}

/// <summary>
/// Whether any tracked "presence" device (e.g. the owner's phone) is currently on the
/// network. Absence of all tracked MACs => away.
/// </summary>
public sealed record PresenceState(int PresentCount, IReadOnlyList<string> PresentMacs)
{
    public bool AnyonePresent => PresentCount > 0;
    public static PresenceState Empty { get; } = new(0, []);
}

/// <summary>
/// A managed node's power-relevant state: online/offline and how many guests are running.
/// "Idle" (the precondition for sleep) means zero running guests.
/// </summary>
public sealed record NodeState(string Name, bool IsOnline, int RunningGuests)
{
    public bool IsIdle => RunningGuests == 0;
}
