using PowerOrchestrator.Core.Config;
using PowerOrchestrator.Core.Policy;

namespace PowerOrchestrator.Service.Api;

public static class CommandEndpoints
{
    public static void MapCommandEndpoints(this WebApplication app)
    {
        // Manual wake — REAL. Works regardless of armed state (operator-initiated).
        app.MapPost("/nodes/{node}/wake", async (
            string node, OrchestratorOptions opts, PowerActions actions, CancellationToken ct) =>
        {
            if (!opts.NodeMacs.ContainsKey(node))
                return Results.BadRequest(new { error = $"unknown node '{node}' (no WoL MAC registered)" });

            await actions.WakeAsync(node, "manual", ct);
            return Results.Accepted($"/nodes/{node}", new { node, action = "wake", trigger = "manual" });
        });

        // Manual sleep — REAL. Only managed (non-sentinel) nodes; deliberate, like the proven test.
        app.MapPost("/nodes/{node}/sleep", async (
            string node, OrchestratorOptions opts, PowerActions actions, CancellationToken ct) =>
        {
            if (string.Equals(node, opts.SentinelNode, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"refusing to sleep the sentinel node '{node}'" });
            if (!opts.ManagedNodes.Contains(node, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"'{node}' is not a managed node ({string.Join(",", opts.ManagedNodes)})" });

            var stopped = await actions.SleepAsync(node, "manual", ct);
            return Results.Ok(new { node, action = "sleep", trigger = "manual", guestsStopped = stopped });
        });

        // Arm automatic sleep — gated on the #191 preconditions. Returns 409 while any are unmet.
        app.MapPost("/policy/arm", (OrchestratorOptions opts) =>
        {
            if (!ArmGuard.CanArm(out var unmet))
                return Results.Conflict(new
                {
                    error = "cannot arm automatic sleep — unmet preconditions",
                    unmet,
                });

            // Preconditions met: arming itself is config-driven (ORCH_ARMED=true + restart) for PR1.
            return Results.Ok(new
            {
                message = "preconditions met — set ORCH_ARMED=true and restart to arm automatic sleep",
                currentlyArmed = opts.Armed,
            });
        });
    }
}
