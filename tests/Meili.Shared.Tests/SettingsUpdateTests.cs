using Meili.Core.Settings;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Tests;

[Collection(nameof(UiPlatformCollection))]
public sealed class SettingsUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "update-vm-" + Guid.NewGuid().ToString("N"));
    private sealed class Theme : IThemeController { public void Apply(AppThemeMode mode) { } public void ApplyUpDownColors(UpDownColorMode mode) { } }
    private sealed class Services : IServiceProvider { public object? GetService(Type type) => null; }
    private UpdateViewModel Create(FakeUpdates updates) => new(new(_directory), new LocalizationService(), new(_directory, DataDirectorySource.CommandLine), updates);
    [Fact]
    public async Task Check_download_and_confirm_are_separate_and_only_successful_preflight_requests_exit()
    {
        var updates = new FakeUpdates(); var ui = new FakeUiPlatform { ConfirmAnswer = false }; Ui.Platform = ui;
        var vm = Create(updates); Assert.Equal(0, updates.Checks); Assert.False(vm.ShowDownloadUpdate);
        await vm.CheckUpdatesCommand.ExecuteAsync(null); Assert.True(vm.ShowDownloadUpdate); Assert.True(vm.HasUpdateNotes); Assert.Contains("0.7.0", vm.UpdateVersionLine);
        await vm.DownloadUpdateCommand.ExecuteAsync(null); Assert.True(vm.CanInstallUpdate); Assert.False(ui.UpdateExitRequested);
        await vm.InstallUpdateCommand.ExecuteAsync(null); Assert.Equal(0, updates.Installs); Assert.False(ui.UpdateExitRequested);
        ui.ConfirmAnswer = true; await vm.InstallUpdateCommand.ExecuteAsync(null); Assert.Equal(1, updates.Installs); Assert.True(ui.UpdateExitRequested);
    }
    [Fact]
    public async Task Failed_verification_never_claims_current_or_enables_installation()
    {
        Ui.Platform = new FakeUiPlatform(); var vm = Create(new() { CheckError = new InvalidDataException("bad signature") });
        await vm.CheckUpdatesCommand.ExecuteAsync(null); Assert.Equal(LocalizationService.Current["Upd_Unverified"], vm.UpdateStatusText); Assert.False(vm.CanInstallUpdate); Assert.False(vm.ShowDownloadUpdate);
    }
    [Fact]
    public async Task Cancellation_keeps_the_app_open_and_allows_download_retry()
    {
        var ui = new FakeUiPlatform(); Ui.Platform = ui; var vm = Create(new() { WaitForCancel = true });
        await vm.CheckUpdatesCommand.ExecuteAsync(null); var download = vm.DownloadUpdateCommand.ExecuteAsync(null);
        Assert.True(vm.IsUpdating); Assert.False(vm.CanCheckUpdates); vm.CancelUpdateCommand.Execute(null); await download;
        Assert.Equal(LocalizationService.Current["Upd_Cancelled"], vm.UpdateStatusText); Assert.False(ui.UpdateExitRequested); Assert.True(vm.CanDownloadUpdate);
    }
    [Fact]
    public async Task Active_MCP_or_install_preflight_failure_does_not_exit_the_application()
    {
        var ui = new FakeUiPlatform(); Ui.Platform = ui; var vm = Create(new() { InstallError = new InvalidOperationException("update_mcp_active") });
        await vm.CheckUpdatesCommand.ExecuteAsync(null); await vm.DownloadUpdateCommand.ExecuteAsync(null); await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Current["Upd_McpActive"], vm.UpdateStatusText); Assert.False(ui.UpdateExitRequested); Assert.True(vm.CanInstallUpdate);
    }
    [Fact]
    public async Task No_release_state_and_release_history_are_explicit()
    {
        var ui = new FakeUiPlatform(); Ui.Platform = ui; var vm = Create(new() { Status = "no_release" });
        await vm.CheckUpdatesCommand.ExecuteAsync(null); Assert.Equal(LocalizationService.Current["Upd_NoRelease"], vm.UpdateStatusText);
        Assert.False(vm.HasUpdateNotes); Assert.False(vm.ShowDownloadUpdate); vm.OpenUpdateHistoryCommand.Execute(null); if (Meili.Automation.UpdateClient.ReleaseHistoryUrl is { } history) Assert.Equal(history, Assert.Single(ui.Opened)); else Assert.Empty(ui.Opened);
    }
    [Fact]
    public async Task Development_or_manual_installations_offer_the_verified_download_folder_without_exit()
    {
        var ui = new FakeUiPlatform(); Ui.Platform = ui; var vm = Create(new() { CanInstall = false });
        await vm.CheckUpdatesCommand.ExecuteAsync(null); await vm.DownloadUpdateCommand.ExecuteAsync(null);
        Assert.False(vm.CanInstallUpdate); Assert.True(vm.ShowUpdateFolder); vm.OpenUpdateFolderCommand.Execute(null);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "fixture-update"), Assert.Single(ui.Opened)); Assert.False(ui.UpdateExitRequested);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
