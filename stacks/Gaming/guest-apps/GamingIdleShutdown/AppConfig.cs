using System;
using System.IO;
using System.Text.Json;

namespace GamingIdleShutdown;

/// <summary>
/// Tunables, loaded from <c>config.json</c> beside the exe if present, else defaults.
/// dryRun is TRUE by default so a fresh deploy only logs — flip it off after observing.
/// </summary>
public sealed class AppConfig
{
    public int PollSeconds { get; set; } = 60;
    public int IdleMinutes { get; set; } = 30;
    public double CpuThresholdPercent { get; set; } = 10;
    public double NetThresholdKBps { get; set; } = 256;
    public int CountdownSeconds { get; set; } = 300;
    public int SnoozeMinutes { get; set; } = 60;
    public bool DryRun { get; set; } = true;

    public static AppConfig Defaults => new();

    public static AppConfig Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg is not null) return cfg;
            }
        }
        catch { /* fall back to defaults on any parse error */ }
        return new AppConfig();
    }

    public override string ToString() =>
        $"poll={PollSeconds}s idle={IdleMinutes}m cpu<{CpuThresholdPercent}% net<{NetThresholdKBps}KB/s " +
        $"countdown={CountdownSeconds}s snooze={SnoozeMinutes}m dryRun={DryRun}";
}
