using PowerOrchestrator.Core.Model;

namespace PowerOrchestrator.Core.Policy;

/// <summary>
/// The brain: given presence + a node's state, decide Wake / Sleep / NoOp. Pure and
/// deterministic given (now, presence, node) — the caller owns the clock, so it's trivially
/// testable. Holds only the away-debounce timer between calls.
/// <para>
/// Rules:
/// <list type="bullet">
/// <item>Someone home + node offline ⇒ <b>Wake</b> (and reset the away timer).</item>
/// <item>Everyone away + node idle (0 guests) for ≥ debounce ⇒ <b>Sleep</b>.</item>
/// <item>Everything else (present+online, away+busy, still-debouncing, already-asleep) ⇒ NoOp.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PowerPolicy(TimeSpan awayDebounce)
{
    private DateTimeOffset? _awaySince;

    /// <summary>Exposed for status/diagnostics: when the current away-streak began (UTC), if any.</summary>
    public DateTimeOffset? AwaySince => _awaySince;

    public Decision Evaluate(DateTimeOffset now, PresenceState presence, NodeState node)
    {
        if (presence.AnyonePresent)
        {
            _awaySince = null;
            return node.IsOnline
                ? Decision.NoOp($"present ({presence.PresentCount} home) + {node.Name} online")
                : Decision.Wake($"present ({presence.PresentCount} home), {node.Name} offline");
        }

        // Everyone away — run the debounce.
        _awaySince ??= now;
        var awayFor = now - _awaySince.Value;

        if (!node.IsOnline)
            return Decision.NoOp($"away + {node.Name} already asleep");
        if (!node.IsIdle)
            return Decision.NoOp($"away but {node.Name} busy ({node.RunningGuests} guest(s) running)");
        if (awayFor < awayDebounce)
            return Decision.NoOp($"away {Fmt(awayFor)} < debounce {Fmt(awayDebounce)}, holding");

        return Decision.Sleep($"away {Fmt(awayFor)} ≥ debounce, {node.Name} idle");
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}m{t.Seconds:00}s";
}
