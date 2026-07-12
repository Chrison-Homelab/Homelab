namespace PowerOrchestrator.Service.Api;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this WebApplication app)
    {
        // Liveness — no dependencies touched.
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        // The orchestrator's current world-view (consumed by the PR2 dashboard).
        app.MapGet("/status", (OrchestratorState state) => Results.Ok(state.Current));
    }
}
