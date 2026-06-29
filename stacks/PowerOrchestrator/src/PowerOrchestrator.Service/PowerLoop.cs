using PowerOrchestrator.Core.Config;
using PowerOrchestrator.Core.Idle;
using PowerOrchestrator.Core.Model;
using PowerOrchestrator.Core.Policy;
using PowerOrchestrator.Core.Presence;

namespace PowerOrchestrator.Service;

/// <summary>
/// The automatic poll loop. Every <see cref="OrchestratorOptions.PollInterval"/> it samples
/// presence + each managed node's state, runs the per-node <see cref="PowerPolicy"/>, and either
/// acts (only when <see cref="OrchestratorOptions.Armed"/>) or — by default — just logs the
/// decision it *would* have taken and records it for the status endpoint / metrics.
/// </summary>
public sealed class PowerLoop(
    OrchestratorOptions options,
    IPresenceSource presence,
    ProxmoxIdleProvider idle,
    PowerActions actions,
    OrchestratorState state,
    Telemetry telemetry,
    ILogger<PowerLoop> logger) : BackgroundService
{
    private readonly Dictionary<string, PowerPolicy> _policies =
        options.ManagedNodes.ToDictionary(
            n => n, _ => new PowerPolicy(options.AwayDebounce), StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "PowerLoop starting: armed={Armed} poll={Poll}s debounce={Debounce}m managed=[{Nodes}] presenceMacs={Macs}",
            options.Armed, options.PollInterval.TotalSeconds, options.AwayDebounce.TotalMinutes,
            string.Join(",", options.ManagedNodes), options.PresenceMacs.Count);
        if (!options.Armed)
            logger.LogWarning("DRY-RUN: automatic policy will only log decisions, never act. Manual commands still act.");

        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "poll cycle failed; will retry next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var activity = telemetry.Activity.StartActivity("poll");

        var presenceState = await presence.GetAsync(ct).ConfigureAwait(false);
        var nodeStates = await idle.GetAsync(options.ManagedNodes, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var reports = new List<NodeReport>(nodeStates.Count);
        foreach (var node in nodeStates)
        {
            var decision = _policies[node.Name].Evaluate(now, presenceState, node);

            if (decision.Kind != DecisionKind.NoOp)
            {
                if (options.Armed)
                    await ActAsync(decision, node.Name, ct).ConfigureAwait(false);
                else
                    logger.LogInformation("[dry-run] {Node}: would {Kind} — {Reason}",
                        node.Name, decision.Kind, decision.Reason);
            }
            else
            {
                logger.LogDebug("{Node}: {Reason}", node.Name, decision.Reason);
            }

            reports.Add(new NodeReport(
                node.Name, node.IsOnline, node.RunningGuests, node.IsIdle,
                decision.Kind.ToString(), decision.Reason));
        }

        var awaySince = _policies.Values.Select(p => p.AwaySince).FirstOrDefault(a => a is not null);
        state.Update(new StatusReport(
            options.Armed, now, presenceState.PresentCount, presenceState.PresentMacs,
            awaySince, reports, ArmGuard.Preconditions()));
    }

    private async Task ActAsync(Decision decision, string node, CancellationToken ct)
    {
        logger.LogInformation("[armed] {Node}: {Kind} — {Reason}", node, decision.Kind, decision.Reason);
        switch (decision.Kind)
        {
            case DecisionKind.Wake:
                await actions.WakeAsync(node, "auto", ct).ConfigureAwait(false);
                break;
            case DecisionKind.Sleep:
                await actions.SleepAsync(node, "auto", ct).ConfigureAwait(false);
                break;
        }
    }
}
