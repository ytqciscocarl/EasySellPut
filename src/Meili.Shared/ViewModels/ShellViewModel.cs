using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meili.Automation;
using Meili.Shared.Services;
using Meili.Core.Settings;
using Meili.Core.Time;

namespace Meili.Shared.ViewModels;

public partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeZoneInfo Beijing = FindBeijing();
    private readonly IUiTimer _clock;
    private bool _presentationVisible=true,_disposed;
    private readonly LocalizationService _loc;
    private readonly DataDirectoryInfo _dataDir;

    [ObservableProperty] public partial string ClockText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MarketStatusText), nameof(MarketStatusTip))] public partial string SessionText { get; set; } = "";
    [ObservableProperty] public partial object? SessionBrush { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MarketStatusTip))] public partial string NetworkText { get; set; } = "";
    [ObservableProperty] public partial string DataDirText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MarketStatusTip))] public partial string SourcesText { get; set; } = "";
    [ObservableProperty] public partial object? SourcesBrush { get; set; }
    [ObservableProperty] public partial string McpStatusText { get; set; } = "";
    [ObservableProperty] public partial string McpStatusTip { get; set; } = "";
    [ObservableProperty] public partial object? McpStatusBrush { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MarketStatusText))] public partial string SourceCountText { get; set; } = "";
    public string MarketStatusText => SessionText + " · " + SourceCountText;
    public string MarketStatusTip => string.Join("\n", new[] { SessionText, SourcesText, NetworkText }.Where(text => !string.IsNullOrWhiteSpace(text)));
    private DateTimeOffset _nextMcpScan;
    private readonly McpConnectionSetup _connection;
    private int _mcpCopyState;
    private DateTimeOffset _mcpCopyUntil;
    private readonly Meili.Data.Routing.HealthTracker _health;

    public ShellViewModel(LocalizationService loc, DataDirectoryInfo dataDir, Meili.Data.DataServices data, McpConnectionSetup? connection = null)
    {
        _loc = loc; _connection = connection ?? McpConnectionSetup.Bundled(AppContext.BaseDirectory, dataDir.Path);
        _health = data.Health;
        _dataDir = dataDir;
        var timer = _clock = Ui.Timer(TimeSpan.FromSeconds(1));
        timer.Tick += ClockTick;
        timer.Start();
        _loc.LanguageChanged += OnLabels;
        Ui.Platform.ColorsChanged += OnLabels;     // 0.4.1: status-bar dots follow a theme switch at once
        Tick();
    }

    internal int McpScanCount{get;private set;}
    public bool IsClockRunning=>_clock.IsEnabled;
    private void ClockTick(object? sender,EventArgs e)=>Tick();
    private void OnLabels(object? sender,EventArgs e){if(_presentationVisible){RefreshMcpStatus();Tick();}}
    public void SetPresentationVisible(bool visible){if(_disposed)return;_presentationVisible=visible;if(visible){_nextMcpScan=default;Tick();if(!_clock.IsEnabled)_clock.Start();}else _clock.Stop();}
    public void Dispose(){if(_disposed)return;_disposed=true;_clock.Stop();_clock.Tick-=ClockTick;_loc.LanguageChanged-=OnLabels;Ui.Platform.ColorsChanged-=OnLabels;}

    /// <summary>Set by <see cref="NetworkController"/> to describe the effective proxy.</summary>
    public string NetworkDescription { get => _networkDescription; set { _networkDescription = value; Tick(); } }
    private string _networkDescription = "";

    private void Tick()
    {
        if(!_presentationVisible||_disposed)return;
        var now = DateTimeOffset.Now;
        if (now >= _nextMcpScan) { RefreshMcpStatus(); _nextMcpScan = now.AddSeconds(5); }
        var et = MarketClock.ToEastern(now);
        var bj = TimeZoneInfo.ConvertTime(now, Beijing);
        ClockText = $"{_loc["Status_Eastern"]} {et:MM-dd HH:mm:ss}   ·   {_loc["Status_Beijing"]} {bj:HH:mm:ss}";
        var session = MarketClock.GetSession(now);
        SessionText = _loc[$"Session_{session}"];
        SessionBrush = Ui.Platform.Resource(session switch
        {
            MarketSession.Regular => "HealthyBrush",
            MarketSession.PreMarket or MarketSession.AfterHours => "DegradedBrush",
            _ => "UnknownBrush",
        });
        NetworkText = string.IsNullOrEmpty(NetworkDescription) ? "" : $"{_loc["Status_Proxy"]}: {NetworkDescription}";
        DataDirText = $"{_loc["Status_DataDir"]}: {_dataDir.Path}";
        var hnow = _health.Now;
        var states = _health.All.Select(h => h.State(hnow)).ToList();
        var ok = states.Count(s => s == Meili.Data.Routing.HealthState.Healthy);
        var bad = states.Count(s => s == Meili.Data.Routing.HealthState.Down);
        SourceCountText = $"{ok}/{states.Count}";
        SourcesText = string.Format(_loc["Status_Sources"], ok, states.Count);
        SourcesBrush = Ui.Platform.Resource(bad > 0 || states.Contains(Meili.Data.Routing.HealthState.Degraded) ? "DegradedBrush" : ok > 0 ? "HealthyBrush" : "UnknownBrush");
    }

    public void RefreshMcpStatus()
    {
        McpScanCount++;
        var status = McpSessions.Read(_dataDir.Path);
        var count = status.Connections.Count; var writable = status.Connections.Any(c => !c.ReadOnly);
        McpStatusText = !status.Available ? _loc["Status_McpUnknown"] : count == 0 ? _loc["Status_McpIdle"] : string.Format(_loc[writable ? "Status_McpWritable" : "Status_McpConnected"], count);
        McpStatusTip = count == 0 ? _loc["Status_McpHint"] : string.Join("\n", status.Connections.Select(c => c.Client + " · " + _loc[c.ReadOnly ? "Status_McpReadOnly" : "Status_McpWriteMode"]));
        if (DateTimeOffset.UtcNow < _mcpCopyUntil) McpStatusText = _loc[_mcpCopyState == 1 ? "Status_McpCopied" : _mcpCopyState == 2 ? "Status_McpCopyFailed" : "Set_AiMissing"];
        McpStatusTip += "\n" + _loc["Status_McpCopyHint"];
        McpStatusBrush = Ui.Platform.Resource(!status.Available || writable ? "DegradedBrush" : count > 0 ? "HealthyBrush" : "UnknownBrush");
    }
    [RelayCommand]
    private async Task CopyMcpSetupAsync()
    {
        if (!File.Exists(_connection.Executable) || (!_connection.Windows && _connection.Executable.Contains("/AppTranslocation/", StringComparison.Ordinal))) _mcpCopyState = 3;
        else try { await Ui.Platform.CopyTextAsync(_connection.ConfigurationPrompt(_loc.Language == "zh-CN")); _mcpCopyState = 1; }
            catch (Exception ex) { Meili.Core.Diagnostics.AppLog.Warn("mcp-setup", "Clipboard unavailable", ex); _mcpCopyState = 2; }
        _mcpCopyUntil = DateTimeOffset.UtcNow.AddSeconds(5); RefreshMcpStatus();
    }

    private static TimeZoneInfo FindBeijing()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.Local;
    }
}
