using System.Threading;
using Avalonia;

namespace GamingIdleShutdown;

internal static class Program
{
    // Avalonia entry point. Single-instance guard so logon autostart can't stack copies.
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\GamingIdleShutdown_SingleInstance", out var createdNew);
        if (!createdNew) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
