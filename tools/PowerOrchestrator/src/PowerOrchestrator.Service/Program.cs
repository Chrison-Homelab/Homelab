using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PowerOrchestrator.Core.Config;
using PowerOrchestrator.Core.Idle;
using PowerOrchestrator.Core.Power;
using PowerOrchestrator.Core.Presence;
using PowerOrchestrator.Service;
using PowerOrchestrator.Service.Api;
using ProxmoxSharp;
using ProxmoxSharp.Api;
using UnifiSharp;

// Dev-box convenience: hydrate process env from secrets.env if present (systemd sets it explicitly).
SecretsEnv.LoadIntoEnvironment();

var builder = WebApplication.CreateBuilder(args);

// Default bind if the operator didn't set ASPNETCORE_URLS / --urls.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) &&
    !args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

var options = OrchestratorOptions.FromEnvironment();

// --- Proxmox creds (mirrors Infrastructure/engine LoadOptions); null degrades gracefully. ---
ProxmoxClientOptions? LoadPve()
{
    var baseUrl = Environment.GetEnvironmentVariable("PROXMOX_BASE_URL");
    var tokenId = Environment.GetEnvironmentVariable("PROXMOX_TOKEN_ID");
    var secret = Environment.GetEnvironmentVariable("PROXMOX_TOKEN_SECRET");
    if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(secret))
        return null;
    var verifyTls = !string.Equals(
        Environment.GetEnvironmentVariable("PROXMOX_VERIFY_TLS"), "false", StringComparison.OrdinalIgnoreCase);
    return new ProxmoxClientOptions
    {
        BaseUrl = new Uri(baseUrl),
        TokenId = tokenId,
        TokenSecret = secret,
        VerifyTls = verifyTls,
    };
}

// --- DI ---
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<OrchestratorState>();
builder.Services.AddSingleton<Telemetry>();
builder.Services.AddSingleton<Func<ProxmoxClientOptions?>>(LoadPve);
// Null when creds are absent — the idle provider degrades to "offline" rather than throwing,
// and SleepAsync refuses (won't poweroff without the ability to stop guests first).
builder.Services.AddSingleton<ProxmoxApiClientFactory>(() =>
{
    var pve = LoadPve();
    return pve is null ? null : ProxmoxApi.Create(pve);
});
builder.Services.AddSingleton(new SshExec(options.SshUser, options.SshKeyPath));
builder.Services.AddSingleton<NodePowerController>();
builder.Services.AddSingleton<ProxmoxIdleProvider>();
builder.Services.AddSingleton<PowerActions>();

// Presence: UniFi when configured + MACs to track; otherwise always-away.
var unifiOptions = UnifiClientOptions.TryFromEnvironment();
if (unifiOptions is not null && options.PresenceMacs.Count > 0)
{
    var unifiClient = UnifiApi.Create(unifiOptions);
    builder.Services.AddSingleton<IPresenceSource>(sp => new UnifiPresenceProvider(
        unifiClient, options.PresenceMacs,
        sp.GetRequiredService<ILogger<UnifiPresenceProvider>>()));
}
else
{
    builder.Services.AddSingleton<IPresenceSource, NullPresenceSource>();
}

builder.Services.AddHostedService<PowerLoop>();

// Blazor web dashboard (PR2) — control + monitor UI on the same host.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// --- OpenTelemetry → OTLP collector (stacks/Monitoring). Only export when an endpoint is set,
//     so local dry-runs don't spam connection errors. ---
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("power-orchestrator"))
    .WithMetrics(m =>
    {
        m.AddMeter(Telemetry.Name)
            .AddRuntimeInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (!string.IsNullOrEmpty(otlpEndpoint)) m.AddOtlpExporter();
    })
    .WithTracing(t =>
    {
        t.AddSource(Telemetry.Name)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (!string.IsNullOrEmpty(otlpEndpoint)) t.AddOtlpExporter();
    });

var app = builder.Build();

// Force the Telemetry singleton to construct now so its observable gauges register at startup.
_ = app.Services.GetRequiredService<Telemetry>();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapStatusEndpoints();
app.MapCommandEndpoints();
app.MapRazorComponents<PowerOrchestrator.Service.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
