using PowerOrchestrator.Core.Model;

namespace PowerOrchestrator.Core.Presence;

/// <summary>A source of network-presence truth (e.g. UniFi connected-clients).</summary>
public interface IPresenceSource
{
    Task<PresenceState> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Fallback when no presence backend is configured (no UniFi creds, or no tracked MACs): always
/// "away". With this in place the automatic loop simply never decides to sleep on presence alone —
/// safe, and exactly what we want until presence is wired up.
/// </summary>
public sealed class NullPresenceSource : IPresenceSource
{
    public Task<PresenceState> GetAsync(CancellationToken ct = default) => Task.FromResult(PresenceState.Empty);
}
