using Avalonia.Input.Platform;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Meili.Mac.Platform;

/// <summary>Avalonia side of <see cref="IUiPlatform"/>. Nothing here blocks the UI thread: dialogs are awaited, never pumped.</summary>
public sealed class MacUiPlatform : IUiPlatform
{
    public MacUiPlatform() => MacTheme.ColorsChanged += (s, e) => ColorsChanged?.Invoke(s, e);

    public event EventHandler? ColorsChanged;

    public void Run(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public IUiTimer CreateTimer(TimeSpan interval) => new Timer(interval);

    public object? Resource(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) ? value : null;

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = MainWindow;
        if (owner is null) return false;
        return await new ConfirmDialog(title, message).ShowDialog<bool>(owner);
    }

    public void OpenNewsArticle(string url, string title)
    {
        if (!NewsReaderPolicy.IsAllowed(url)) throw new ArgumentException("Unsupported article URL", nameof(url));
        Run(()=>{if(MainWindow is MainWindow window)window.OpenNewsArticle(url,title);});
    }

    public async Task CopyTextAsync(string text)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var clipboard = window is null ? null : TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is null) throw new NotSupportedException("Clipboard unavailable");
        await clipboard.SetTextAsync(text);
    }

    public void ExitForUpdate()
    {
        if(Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)throw new NotSupportedException();
        (desktop.MainWindow as MainWindow)?.AllowClose();desktop.Shutdown();
    }
    public void OpenUrl(string url) => Open(url);

    public void OpenFolder(string path) => Open(path);

    /// <summary>Hands the URL or folder to Launch Services (`open`), which returns at once.</summary>
    private static void Open(string target)
    {
        var psi = new ProcessStartInfo("open") { UseShellExecute = false };
        psi.ArgumentList.Add(target);
        Process.Start(psi)?.Dispose();
    }

    public async Task<string?> PickSavePathAsync(string title, string suggestedName)
    {
        var owner = MainWindow;
        if (owner is null) return null;
        var loc = LocalizationService.Current;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName + ".xlsx",
            DefaultExtension = "xlsx",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(loc["Ex_XlsxType"]) { Patterns = ["*.xlsx"], AppleUniformTypeIdentifiers = ["org.openxmlformats.spreadsheetml.sheet"] },
                new FilePickerFileType(loc["Ex_CsvType"]) { Patterns = ["*.csv"], AppleUniformTypeIdentifiers = ["public.comma-separated-values-text"] },
            ],
        });
        return file?.TryGetLocalPath();
    }

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>Background priority: ticks never preempt input or rendering.</summary>
    private sealed class Timer(TimeSpan interval) : IUiTimer
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = interval };

        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
        public bool IsEnabled => _timer.IsEnabled;
        public event EventHandler? Tick { add => _timer.Tick += value; remove => _timer.Tick -= value; }
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}
