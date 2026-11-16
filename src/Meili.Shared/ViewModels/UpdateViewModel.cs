using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meili.Automation;
using Meili.Core.Settings;
using Meili.Shared.Services;

namespace Meili.Shared.ViewModels;

/// <summary>Desktop-owned, shared update state. Checking never downloads or installs.</summary>
public partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _loc;
    private readonly string DataDirectory;
    public string Version => UpdateClient.CurrentVersion.ToString(3);
    public UpdateViewModel(SettingsStore store, LocalizationService loc, DataDirectoryInfo dataDir, IUpdateCoordinator? updater = null)
    {
        _loc = loc; DataDirectory = dataDir.Path;
        _updater = updater ?? new UpdateCoordinator(store, dataDir.Path, AppContext.BaseDirectory);
        _loc.LanguageChanged += OnLanguageChanged;
        ReadUpdateResult();
        if (!_updater.IsConfigured) _updateState = "Upd_Disabled";
    }
    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshUpdateBindings();
    private readonly IUpdateCoordinator _updater;
    private UpdateRelease? _updateRelease;
    private string? _updateArchive;
    private string _updateState="Upd_Idle";
    private CancellationTokenSource? _updateStop;
    [ObservableProperty] public partial bool IsUpdating {get;set;}
    [ObservableProperty] public partial double UpdateProgress {get;set;}
    public string UpdateVersionLine=>"v"+Version+(_updateRelease is null?"":"  →  v"+_updateRelease.Manifest.Version);
    public string UpdateStatusText=>_loc[_updateState];
    public string UpdateNotes=>_updateRelease?.Notes??"";
    public bool HasUpdateNotes=>!string.IsNullOrWhiteSpace(UpdateNotes);
    public bool ShowDownloadUpdate=>_updateRelease is not null&&_updateArchive is null;
    public bool ShowInstallUpdate=>_updateArchive is not null;
    public bool ShowUpdateProgress=>IsUpdating;
    public bool ShowUpdateFolder=>_updateArchive is not null&&(!_updater.CanInstall||_updateState=="Upd_Failed");
    public bool IsUpdateIndeterminate=>_updateState!="Upd_Downloading";
    public bool CanCheckUpdates=>_updater.IsConfigured && !IsUpdating;
    public bool HasUpdateHistory => UpdateClient.ReleaseHistoryUrl is not null;
    public bool CanDownloadUpdate=>!IsUpdating&&ShowDownloadUpdate;
    public bool CanInstallUpdate=>!IsUpdating&&ShowInstallUpdate&&_updater.CanInstall;
    partial void OnIsUpdatingChanged(bool value)=>RefreshUpdateBindings();
    private void RefreshUpdateBindings()
    {
        foreach(var property in new[]{nameof(UpdateVersionLine),nameof(UpdateStatusText),nameof(UpdateNotes),nameof(HasUpdateNotes),nameof(ShowDownloadUpdate),nameof(ShowInstallUpdate),nameof(ShowUpdateProgress),nameof(ShowUpdateFolder),nameof(IsUpdateIndeterminate),nameof(CanCheckUpdates),nameof(CanDownloadUpdate),nameof(CanInstallUpdate),nameof(HasAvailableUpdate),nameof(UpdateBadgeText),nameof(UpdatePromptTitle)})OnPropertyChanged(property);
        CheckUpdatesCommand.NotifyCanExecuteChanged();DownloadUpdateCommand.NotifyCanExecuteChanged();InstallUpdateCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute=nameof(CanCheckUpdates))]
    private Task CheckUpdatesAsync() { _startupAttempted = true; return CheckAsync(CancellationToken.None); }
    private Task CheckAsync(CancellationToken cancellation)=>RunUpdateAsync("Upd_Checking",async ct=>
    {
        _updateArchive=null;_updateRelease=null;
        var result=await _updater.CheckAsync(ct);_updateRelease=result.Release;
        _updateState=result.Status switch{"available"=>"Upd_Available","current"=>"Upd_Current","no_release"=>"Upd_NoRelease","disabled"=>"Upd_Disabled",_=>"Upd_Failed"};
    }, cancellation);
    [RelayCommand(CanExecute=nameof(CanDownloadUpdate))]
    private Task DownloadUpdateAsync()=>RunUpdateAsync("Upd_Downloading",async ct=>
    {
        UpdateProgress=0;
        _updateArchive=await _updater.DownloadAsync(_updateRelease!,new Progress<double>(value=>UpdateProgress=value),ct);
        _updateState=_updater.CanInstall?"Upd_Ready":"Upd_Manual";
    });
    [RelayCommand(CanExecute=nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if(!CanInstallUpdate)return;
        if(!await Ui.Platform.ConfirmAsync(_loc["Upd_ConfirmTitle"],string.Format(_loc["Upd_Confirm"],_updateRelease!.Manifest.Version)))return;
        await RunUpdateAsync("Upd_Preparing",async ct=>
        {
            await _updater.PrepareInstallAsync(_updateRelease!,_updateArchive!,ct);
            _updateState="Upd_Restarting";RefreshUpdateBindings();Ui.Platform.ExitForUpdate();
        });
    }
    [RelayCommand]
    private void CancelUpdate()=>_updateStop?.Cancel();
    [RelayCommand]
    private void OpenUpdateFolder(){if(_updateArchive is not null)Ui.Platform.OpenFolder(Path.GetDirectoryName(_updateArchive)!);}
    [RelayCommand(CanExecute = nameof(HasUpdateHistory))]
    private void OpenUpdateHistory() { if (UpdateClient.ReleaseHistoryUrl is { } url) Ui.Platform.OpenUrl(url); }
    private async Task RunUpdateAsync(string state,Func<CancellationToken,Task> work, CancellationToken cancellation = default)
    {
        if(IsUpdating || _disposed || !_updater.IsConfigured)return;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);_updateStop=stop;_updateState=state;IsUpdating=true;
        try{await work(stop.Token);}
        catch(OperationCanceledException){_updateState=stop.IsCancellationRequested?"Upd_Cancelled":"Upd_Timeout";}
        catch(Exception ex)
        {
            Meili.Core.Diagnostics.AppLog.Warn("updater","Update action failed",ex);
            _updateState=ex is System.Security.Cryptography.CryptographicException or InvalidDataException?"Upd_Unverified":ex is InvalidOperationException && ex.Message=="update_mcp_active"?"Upd_McpActive":"Upd_Failed";
        }
        finally{_updateStop=null;IsUpdating=false;RefreshUpdateBindings();OfferPrompt();}
    }
    private void ReadUpdateResult()
    {
        try
        {
            var file=new FileInfo(UpdateInstaller.ResultFile(DataDirectory));if(!file.Exists||file.Length>8192||file.LinkTarget is not null)return;
            var result=System.Text.Json.JsonSerializer.Deserialize<UpdateInstallResult>(File.ReadAllText(file.FullName),UpdateInstaller.Json);
            if(result?.Status=="installed"&&result.Version==Version)_updateState="Upd_Installed";
            else if(result?.Status is "failed" or "restart_required")_updateState="Upd_PreviousFailed";
        }
        catch(Exception ex){Meili.Core.Diagnostics.AppLog.Warn("updater","Previous update status unavailable",ex);}
    }
    public bool HasAvailableUpdate => _updateRelease is not null;
    public string UpdateBadgeText => _updateRelease is null ? "" : string.Format(_loc[ShowInstallUpdate ? "Upd_BadgeReady" : "Upd_BadgeAvailable"], _updateRelease.Manifest.Version);
    public string UpdatePromptTitle => _updateRelease is null ? _loc["Upd_Title"] : string.Format(_loc["Upd_PromptTitle"], _updateRelease.Manifest.Version);
    [ObservableProperty] public partial bool IsUpdatePromptOpen { get; set; }
    private readonly HashSet<string> _notifiedVersions = new(StringComparer.Ordinal);
    private bool _visible, _started, _startupAttempted, _disposed;
    private CancellationTokenSource? _startupStop;

    /// <summary>Check once after visible startup. No polling or hidden-window dialogs.</summary>
    public void SetPresentationVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (!visible) { _startupStop?.Cancel(); IsUpdatePromptOpen = false; return; }
        OfferPrompt();
        ScheduleStartupCheck();
    }
    public void StartNotifications()
    {
        if (_started || _disposed) return;
        _started = true; ScheduleStartupCheck();
    }
    private void ScheduleStartupCheck()
    {
        if (_updater.IsConfigured && _started && _visible && !_startupAttempted && _updateRelease is null) _ = CheckAfterStartupAsync();
    }
    internal TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(10);
    private async Task CheckAfterStartupAsync()
    {
        using var stop = new CancellationTokenSource();
        _startupStop = stop;
        try
        {
            await Task.Delay(StartupDelay, stop.Token);
            if (!_visible || _disposed || IsUpdating || _startupAttempted || _updateRelease is not null) return;
            _startupAttempted = true;
            await CheckAsync(stop.Token);
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_startupStop, stop)) _startupStop = null; }
    }
    private void OfferPrompt()
    {
        if (!_disposed && _visible && !IsUpdating && _updateRelease is { } release && _notifiedVersions.Add(release.Manifest.Version))
            IsUpdatePromptOpen = true;
    }
    [RelayCommand]
    private void OpenUpdatePrompt()
    {
        if (HasAvailableUpdate && !_disposed) IsUpdatePromptOpen = true;
    }
    [RelayCommand]
    private void DismissUpdatePrompt() => IsUpdatePromptOpen = false;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _startupStop?.Cancel(); _updateStop?.Cancel();
        IsUpdatePromptOpen = false; _loc.LanguageChanged -= OnLanguageChanged;
    }

}
