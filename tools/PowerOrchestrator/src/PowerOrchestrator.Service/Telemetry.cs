using System.Diagnostics;
using System.Diagnostics.Metrics;
using PowerOrchestrator.Core.Model;

namespace PowerOrchestrator.Service;

/// <summary>
/// OpenTelemetry instruments. <see cref="Name"/> is registered as both the Meter and the
/// ActivitySource so the OTLP exporter picks them up. Observable gauges read the current
/// <see cref="OrchestratorState"/>; the actions counter is incremented on each wake/sleep.
/// </summary>
public sealed class Telemetry : IDisposable
{
    public const string Name = "PowerOrchestrator";

    private readonly Meter _meter = new(Name);
    public ActivitySource Activity { get; } = new(Name);

    private readonly Counter<long> _actions;

    public Telemetry(OrchestratorState state)
    {
        _meter.CreateObservableGauge("orchestrator.armed",
            () => state.Current.Armed ? 1 : 0,
            description: "1 if automatic policy is armed, 0 if dry-run.");

        _meter.CreateObservableGauge("orchestrator.presence.present_count",
            () => state.Current.PresentCount,
            description: "Number of tracked presence devices currently on the network.");

        _meter.CreateObservableGauge("orchestrator.node.online",
            () => state.Current.Nodes.Select(n => new Measurement<int>(n.Online ? 1 : 0, Tag(n))),
            description: "1 if the managed node is online, 0 if asleep/offline.");

        _meter.CreateObservableGauge("orchestrator.node.running_guests",
            () => state.Current.Nodes.Select(n => new Measurement<int>(n.RunningGuests, Tag(n))),
            description: "Running guests on the managed node.");

        _meter.CreateObservableGauge("orchestrator.node.idle",
            () => state.Current.Nodes.Select(n => new Measurement<int>(n.Idle ? 1 : 0, Tag(n))),
            description: "1 if the managed node has no running guests.");

        _actions = _meter.CreateCounter<long>("orchestrator.actions",
            description: "Power actions taken (wake/sleep), tagged by node, action, trigger and result.");
    }

    public void RecordAction(string node, string action, string trigger, bool ok) =>
        _actions.Add(1,
            new KeyValuePair<string, object?>("node", node),
            new KeyValuePair<string, object?>("action", action),
            new KeyValuePair<string, object?>("trigger", trigger),
            new KeyValuePair<string, object?>("result", ok ? "ok" : "error"));

    private static KeyValuePair<string, object?> Tag(NodeReport n) => new("node", n.Name);

    public void Dispose()
    {
        _meter.Dispose();
        Activity.Dispose();
    }
}
