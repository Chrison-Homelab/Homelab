using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace GamingIdleShutdown;

public enum CountdownResult { Cancel, Snooze, ShutdownNow, Expired }

public partial class CountdownWindow : Window
{
    public event Action<CountdownResult>? Result;

    private readonly DispatcherTimer _timer;
    private int _remaining;
    private bool _done;

    // Parameterless ctor for the XAML previewer.
    public CountdownWindow() : this(300) { }

    public CountdownWindow(int seconds)
    {
        AvaloniaXamlLoader.Load(this);

        _remaining = Math.Max(1, seconds);
        UpdateLabel();

        this.FindControl<Button>("CancelBtn")!.Click += (_, _) => Finish(CountdownResult.Cancel);
        this.FindControl<Button>("SnoozeBtn")!.Click += (_, _) => Finish(CountdownResult.Snooze);
        this.FindControl<Button>("ShutdownBtn")!.Click += (_, _) => Finish(CountdownResult.ShutdownNow);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        _remaining--;
        if (_remaining <= 0) { Finish(CountdownResult.Expired); return; }
        UpdateLabel();
    }

    private void UpdateLabel()
    {
        var lbl = this.FindControl<TextBlock>("CountdownText");
        if (lbl is not null)
            lbl.Text = TimeSpan.FromSeconds(Math.Max(0, _remaining)).ToString(@"m\:ss");
    }

    private void Finish(CountdownResult result)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Result?.Invoke(result);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_done)                      // window closed via the chrome 'X' == Cancel
        {
            _done = true;
            _timer.Stop();
            Result?.Invoke(CountdownResult.Cancel);
        }
    }
}
