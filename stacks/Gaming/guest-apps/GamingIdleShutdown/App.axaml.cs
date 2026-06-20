using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace GamingIdleShutdown;

public class App : Application
{
    private AppConfig _cfg = AppConfig.Defaults;
    private IdleSampler _sampler = null!;
    private DispatcherTimer? _pollTimer;
    private DateTime _lastActiveUtc;
    private DateTime _snoozeUntilUtc = DateTime.MinValue;
    private CountdownWindow? _countdown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Background app: no main window, never exit just because no window is open.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _cfg = AppConfig.Load();
        _sampler = new IdleSampler();
        _lastActiveUtc = DateTime.UtcNow;
        Log($"started — {_cfg}");

        TrySetupTray();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_cfg.PollSeconds) };
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();

        base.OnFrameworkInitializationCompleted();
    }

    private void Poll()
    {
        try
        {
            if (_countdown is not null) return;                 // already counting down
            var now = DateTime.UtcNow;
            if (now < _snoozeUntilUtc) { _lastActiveUtc = now; return; }

            var s = _sampler.Sample(_cfg.PollSeconds);
            var active = s.InputIdleMs < _cfg.PollSeconds * 2000   // input within ~2 polls
                         || s.CpuPercent >= _cfg.CpuThresholdPercent
                         || s.NetKBytesPerSec >= _cfg.NetThresholdKBps;
            if (active) { _lastActiveUtc = now; return; }

            var idleFor = now - _lastActiveUtc;
            Log($"idle {idleFor.TotalMinutes:F1}m  cpu={s.CpuPercent:F1}%  net={s.NetKBytesPerSec:F0}KB/s  inputIdle={s.InputIdleMs / 1000}s");
            if (idleFor >= TimeSpan.FromMinutes(_cfg.IdleMinutes))
                ShowCountdown();
        }
        catch (Exception ex) { Log("poll error: " + ex.Message); }
    }

    private void ShowCountdown()
    {
        Log("idle threshold reached -> countdown shown");
        _countdown = new CountdownWindow(_cfg.CountdownSeconds);
        _countdown.Result += OnCountdownResult;
        _countdown.Show();
        _countdown.Activate();
    }

    private void OnCountdownResult(CountdownResult result)
    {
        _countdown = null;
        var now = DateTime.UtcNow;
        switch (result)
        {
            case CountdownResult.Cancel:
                _lastActiveUtc = now; Log("countdown cancelled"); break;
            case CountdownResult.Snooze:
                _snoozeUntilUtc = now.AddMinutes(_cfg.SnoozeMinutes); _lastActiveUtc = now;
                Log($"snoozed {_cfg.SnoozeMinutes}m"); break;
            case CountdownResult.ShutdownNow:
                Shutdown("user chose shut down now"); break;
            case CountdownResult.Expired:
                Shutdown("countdown expired"); break;
        }
    }

    private void Shutdown(string reason)
    {
        if (_cfg.DryRun)
        {
            Log($"[DRY RUN] would shut down ({reason})");
            _lastActiveUtc = DateTime.UtcNow;     // reset so we don't immediately re-trigger
            return;
        }
        Log($"shutting down ({reason})");
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
                { CreateNoWindow = true, UseShellExecute = false });
            else
                Log("non-Windows host: shutdown skipped");
        }
        catch (Exception ex) { Log("shutdown failed: " + ex.Message); }
    }

    private void TrySetupTray()
    {
        try
        {
            var tray = new TrayIcon
            {
                ToolTipText = "Gaming VM idle auto-shutdown",
                Icon = BuildTrayIcon(),
                IsVisible = true,
            };
            var menu = new NativeMenu();
            var snooze = new NativeMenuItem("Snooze 1h");
            snooze.Click += (_, _) =>
            {
                _snoozeUntilUtc = DateTime.UtcNow.AddMinutes(_cfg.SnoozeMinutes);
                _lastActiveUtc = DateTime.UtcNow;
                Log("snoozed via tray");
            };
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) =>
            {
                Log("quit via tray");
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            };
            menu.Items.Add(snooze);
            menu.Items.Add(quit);
            tray.Menu = menu;
            TrayIcon.SetIcons(this, new TrayIcons { tray });
        }
        catch (Exception ex) { Log("tray setup failed (continuing headless): " + ex.Message); }
    }

    private static WindowIcon BuildTrayIcon()
    {
        var bmp = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
            ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)), null, new Rect(1, 1, 30, 30));
        using var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamingIdleShutdown", "log.txt");

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }
}
