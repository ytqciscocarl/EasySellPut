using Meili.Core.Settings;

namespace Meili.Shared.Services;

/// <summary>
/// The few things view-models need from the UI toolkit. Implemented by the WPF front end (Windows) and the Avalonia
/// front end (macOS); set once at start-up through <see cref="Ui.Platform"/>.
/// </summary>
public interface IUiPlatform
{
    /// <summary>Runs <paramref name="action"/> on the UI thread: inline when already there, otherwise posted (never blocks the caller).</summary>
    void Run(Action action);

    /// <summary>A UI-thread timer at background priority (ticks never preempt input or rendering).</summary>
    IUiTimer CreateTimer(TimeSpan interval);

    /// <summary>A theme resource (brush) by key, e.g. "UpBrush"; bound to Foreground/Background as-is.</summary>
    object? Resource(string key);

    /// <summary>Raised after the theme or the red/green convention changes, so cached brushes are looked up again.</summary>
    event EventHandler? ColorsChanged;

    Task<bool> ConfirmAsync(string title, string message);

    void ExitForUpdate() => throw new NotSupportedException("Application shutdown is unavailable.");

    void OpenUrl(string url);

    void OpenNewsArticle(string url, string title);

    void OpenFolder(string path);

    Task CopyTextAsync(string text) => Task.FromException(new NotSupportedException("Clipboard unavailable"));

    /// <summary>Native save dialog offering .xlsx and .csv. Null when the user cancels.</summary>
    Task<string?> PickSavePathAsync(string title, string suggestedName);
}

public interface IUiTimer
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    event EventHandler? Tick;
    void Start();
    void Stop();
}

/// <summary>Theme switching (light/dark/system and the red-up/green-up convention).</summary>
public interface IThemeController
{
    void Apply(AppThemeMode mode);
    void ApplyUpDownColors(UpDownColorMode mode);
}

/// <summary>Alert delivery: Windows toasts / tray balloons, macOS notifications.</summary>
public interface IAlertNotifier
{
    void ShowAlert(Meili.Core.Alerts.PriceAlert alert, Meili.Core.Alerts.AlertEvalResult result, Meili.Core.Models.Quote? quote);
    void ShowIvAlert(Meili.Core.Alerts.PriceAlert alert, Meili.Core.Alerts.AlertEvalResult result, Meili.Core.Alerts.IvSample sample);
    void ShowMonitorInterrupted(Meili.Core.Alerts.MonitorHealth health);
    void ShowMonitorRecovered();
}

/// <summary>Static access for field initializers and XAML-created objects (same pattern as <see cref="AppServices"/>).</summary>
public static class Ui
{
    public static IUiPlatform Platform { get; set; } = null!;
    public static IUiTimer Timer(TimeSpan interval = default) => Platform.CreateTimer(interval);
}

/// <summary>Pages the view-models can ask the shell to show (each front end maps them to its own page types).</summary>
public enum AppPage { Dashboard, Watchlist, Charts, Options, Strategy, News, Alerts, DataSources, Settings }
