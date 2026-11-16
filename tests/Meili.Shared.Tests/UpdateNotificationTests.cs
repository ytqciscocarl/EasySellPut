using Meili.Core.Settings;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Tests;

[Collection(nameof(UiPlatformCollection))]
public sealed class UpdateNotificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "update-notices-" + Guid.NewGuid().ToString("N"));
    private UpdateViewModel Create(FakeUpdates source, LocalizationService? loc = null)
    {
        Ui.Platform = new FakeUiPlatform();
        return new(new(_directory), loc ?? new LocalizationService(), new(_directory, DataDirectorySource.CommandLine), source) { StartupDelay = TimeSpan.Zero };
    }
    [Fact]
    public async Task Available_version_prompts_once_footer_survives_later_and_reopens_without_download()
    {
        var source = new FakeUpdates(); using var vm = Create(source);
        vm.SetPresentationVisible(true);
        Assert.False(vm.HasAvailableUpdate); Assert.False(vm.IsUpdatePromptOpen);
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.HasAvailableUpdate); Assert.True(vm.IsUpdatePromptOpen); Assert.Contains("0.7.0", vm.UpdateBadgeText);
        vm.DismissUpdatePromptCommand.Execute(null);
        Assert.False(vm.IsUpdatePromptOpen); Assert.True(vm.HasAvailableUpdate);
        vm.SetPresentationVisible(false); vm.SetPresentationVisible(true);
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.False(vm.IsUpdatePromptOpen);
        vm.OpenUpdatePromptCommand.Execute(null); Assert.True(vm.IsUpdatePromptOpen);
        Assert.Equal(0, source.Downloads); Assert.Equal(0, source.Installs);
        vm.DismissUpdatePromptCommand.Execute(null); source.ReleaseVersion = "0.8.0";
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.IsUpdatePromptOpen); Assert.Contains("0.8.0", vm.UpdateBadgeText);
    }
    [Theory]
    [InlineData("current")]
    [InlineData("no_release")]
    public async Task No_update_has_no_footer_or_popup(string status)
    {
        using var vm = Create(new() { Status = status }); vm.SetPresentationVisible(true);
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        vm.OpenUpdatePromptCommand.Execute(null);
        Assert.False(vm.HasAvailableUpdate); Assert.False(vm.IsUpdatePromptOpen); Assert.Equal("", vm.UpdateBadgeText);
    }
    [Fact]
    public async Task Hidden_check_defers_prompt_until_restore_and_signature_failure_never_notifies()
    {
        var source = new FakeUpdates(); using var vm = Create(source);
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.HasAvailableUpdate); Assert.False(vm.IsUpdatePromptOpen);
        vm.SetPresentationVisible(true); Assert.True(vm.IsUpdatePromptOpen);
        vm.SetPresentationVisible(false); Assert.False(vm.IsUpdatePromptOpen);
        source.CheckError = new InvalidDataException("bad signature");
        await vm.CheckUpdatesCommand.ExecuteAsync(null); vm.SetPresentationVisible(true);
        Assert.False(vm.HasAvailableUpdate); Assert.False(vm.IsUpdatePromptOpen);
    }
    [Fact]
    public void Startup_checks_once_only_after_visible_production_start_and_never_polls()
    {
        var source = new FakeUpdates { Status = "current" }; using var vm = Create(source);
        vm.StartNotifications(); Assert.Equal(0, source.Checks);
        vm.SetPresentationVisible(true); Assert.Equal(1, source.Checks);
        vm.SetPresentationVisible(false); vm.SetPresentationVisible(true); vm.StartNotifications();
        Assert.Equal(1, source.Checks); Assert.False(vm.HasAvailableUpdate);
    }
    [Fact]
    public async Task Hiding_cancels_inflight_startup_check_without_popup_or_retry_loop()
    {
        var source = new FakeUpdates { WaitForCheckCancel = true }; using var vm = Create(source);
        vm.SetPresentationVisible(true); vm.StartNotifications(); Assert.True(vm.IsUpdating);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsUpdating) && !vm.IsUpdating) finished.TrySetResult(); };
        vm.SetPresentationVisible(false); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.HasAvailableUpdate); Assert.False(vm.IsUpdatePromptOpen);
        vm.SetPresentationVisible(true); Assert.Equal(1, source.Checks);
    }
    [Fact]
    public async Task Ready_badge_and_localized_title_share_the_existing_download_state()
    {
        var loc = new LocalizationService(); using var vm = Create(new(), loc);
        await vm.CheckUpdatesCommand.ExecuteAsync(null); await vm.DownloadUpdateCommand.ExecuteAsync(null);
        Assert.Contains("待安装", vm.UpdateBadgeText);
        loc.Apply("en-US"); Assert.Equal("v0.7.0 ready", vm.UpdateBadgeText); Assert.Equal("Version 0.7.0 is available", vm.UpdatePromptTitle);
    }
    [Fact]
    public async Task Unconfigured_development_coordinator_never_checks_or_schedules_notifications()
    {
        var source = new FakeUpdates { IsConfigured = false }; using var vm = Create(source);
        vm.SetPresentationVisible(true); vm.StartNotifications();
        Assert.False(vm.CanCheckUpdates); Assert.Equal(LocalizationService.Current["Upd_Disabled"], vm.UpdateStatusText);
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(0, source.Checks); Assert.False(vm.HasAvailableUpdate); Assert.False(vm.IsUpdatePromptOpen);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
