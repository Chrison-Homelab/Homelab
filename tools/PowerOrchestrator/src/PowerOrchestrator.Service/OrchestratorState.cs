using PowerOrchestrator.Core.Config;
using PowerOrchestrator.Core.Model;
using PowerOrchestrator.Core.Policy;

namespace PowerOrchestrator.Service;

/// <summary>
/// Thread-safe holder of the latest world-view. The <see cref="PowerLoop"/> swaps in a fresh
/// immutable <see cref="StatusReport"/> after each poll; the status endpoint and the OTel
/// observable gauges read the current one. Reference swaps of an immutable record are atomic.
/// </summary>
public sealed class OrchestratorState
{
    private volatile StatusReport _report;

    public OrchestratorState(OrchestratorOptions options)
    {
        _report = new StatusReport(
            Armed: options.Armed,
            LastPollUtc: null,
            PresentCount: 0,
            PresentMacs: [],
            AwaySince: null,
            Nodes: options.ManagedNodes
                .Select(n => new NodeReport(n, Online: false, RunningGuests: 0, Idle: false, "-", "startup"))
                .ToList(),
            ArmPreconditions: ArmGuard.Unknown());
    }

    public StatusReport Current => _report;

    public void Update(StatusReport report) => _report = report;
}
