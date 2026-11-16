namespace Meili.Shared.Tests;

/// <summary>Synchronous stand-in for the UI toolkit; timers tick only when a test calls <see cref="FakeTimer.Fire"/>.</summary>
public sealed class FakeUiPlatform : IUiPlatform
{
    public List<FakeTimer> Timers { get; } = [];
    public string? SavePath { get; set; }
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Opened { get; } = [];
    public int? SaveThreadId { get; private set; }

    public event EventHandler? ColorsChanged;

    public void Run(Action action) => action();

    public IUiTimer CreateTimer(TimeSpan interval)
    {
        var t = new FakeTimer { Interval = interval };
        Timers.Add(t);
        return t;
    }

    public object? Resource(string key) => key;

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmAnswer);

    public bool FailCopy { get; set; }
    public string? ClipboardText { get; private set; }
    public Task CopyTextAsync(string text) { if (FailCopy) throw new InvalidOperationException("Clipboard unavailable"); ClipboardText = text; return Task.CompletedTask; }
    public List<(string Url, string Title)> Articles { get; } = [];
    public void OpenNewsArticle(string url, string title) => Articles.Add((url, title));
    public bool UpdateExitRequested {get;private set;}
    public void ExitForUpdate()=>UpdateExitRequested=true;
    public void OpenUrl(string url) => Opened.Add(url);

    public void OpenFolder(string path) => Opened.Add(path);

    public Task<string?> PickSavePathAsync(string title, string suggestedName)
    {
        SaveThreadId = Environment.CurrentManagedThreadId;
        return Task.FromResult(SavePath);
    }

    public void RaiseColorsChanged() => ColorsChanged?.Invoke(this, EventArgs.Empty);

    public sealed class FakeTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
