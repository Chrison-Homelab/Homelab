using PowerOrchestrator.Core.Power;

namespace PowerOrchestrator.Service;

/// <summary>
/// Thin wrapper that both the automatic loop (trigger="auto") and the manual control endpoints
/// (trigger="manual") call, so every real power action is traced + counted identically.
/// </summary>
public sealed class PowerActions(
    NodePowerController controller,
    Telemetry telemetry,
    ILogger<PowerActions> logger)
{
    public async Task WakeAsync(string node, string trigger, CancellationToken ct = default)
    {
        using var activity = telemetry.Activity.StartActivity("wake");
        activity?.SetTag("node", node);
        activity?.SetTag("trigger", trigger);
        try
        {
            await controller.WakeAsync(node, ct).ConfigureAwait(false);
            telemetry.RecordAction(node, "wake", trigger, ok: true);
        }
        catch (Exception ex)
        {
            telemetry.RecordAction(node, "wake", trigger, ok: false);
            logger.LogError(ex, "wake {Node} ({Trigger}) failed", node, trigger);
            throw;
        }
    }

    public async Task<int> SleepAsync(string node, string trigger, CancellationToken ct = default)
    {
        using var activity = telemetry.Activity.StartActivity("sleep");
        activity?.SetTag("node", node);
        activity?.SetTag("trigger", trigger);
        try
        {
            var stopped = await controller.SleepAsync(node, ct).ConfigureAwait(false);
            telemetry.RecordAction(node, "sleep", trigger, ok: true);
            return stopped;
        }
        catch (Exception ex)
        {
            telemetry.RecordAction(node, "sleep", trigger, ok: false);
            logger.LogError(ex, "sleep {Node} ({Trigger}) failed", node, trigger);
            throw;
        }
    }
}
