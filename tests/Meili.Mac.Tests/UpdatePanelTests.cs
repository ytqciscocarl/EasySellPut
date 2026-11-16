using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views;
using Meili.Shared.Tests;

namespace Meili.Mac.Tests;

public sealed class UpdatePanelTests
{
    [AvaloniaTheory]
    [InlineData("zh-CN", false)]
    [InlineData("en-US", true)]
    public async Task Update_panel_exposes_notes_download_progress_ready_and_failure_without_executing_an_installer(string language, bool dark)
    {
        MacHost.EnsureComposed(); var loc = AppServices.Get<LocalizationService>(); var original = loc.Language;
        var theme = AppServices.Get<MacTheme>(); loc.Apply(language); theme.Apply(dark ? AppThemeMode.Dark : AppThemeMode.Light);
        var directory = Path.Combine(MacHost.DataDir, "update-panel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var updates = new FakeUpdates(); var vm = new SettingsViewModel(new(directory), loc, theme, new(directory, DataDirectorySource.CommandLine), AppServices.Provider, updater: updates);
        var state = vm.Updates;
        var window = new MainWindow { Width = 1100, Height = 760 };
        try
        {
            window.Show(); window.Navigate(AppPage.Settings); var page = (SettingsPage)window.CurrentView!; page.DataContext = vm; MacHost.Pump();
            var panel = page.FindControl<Border>("UpdatePanel")!; Assert.Equal(0, updates.Checks);
            await state.CheckUpdatesCommand.ExecuteAsync(null); MacHost.Pump();
            Assert.True(state.CanDownloadUpdate); Assert.False(state.ShowInstallUpdate);
            var notes = panel.GetVisualDescendants().OfType<Expander>().Single(); Assert.False(notes.IsExpanded); notes.IsExpanded = true; MacHost.Pump();
            Save(panel, $"update-available-{language}-{dark}");
            updates.WaitForCancel = true; var downloading = state.DownloadUpdateCommand.ExecuteAsync(null); MacHost.Pump();
            Assert.True(state.IsUpdating); Assert.InRange(state.UpdateProgress, 1, 100); Save(panel, $"update-downloading-{language}-{dark}");
            state.CancelUpdateCommand.Execute(null); await downloading;
            updates.WaitForCancel = false; await state.DownloadUpdateCommand.ExecuteAsync(null); MacHost.Pump();
            Assert.True(state.CanInstallUpdate); Assert.Equal(0, updates.Installs); Save(panel, $"update-ready-{language}-{dark}");
            updates.CheckError = new InvalidDataException("fixture signature rejection"); await state.CheckUpdatesCommand.ExecuteAsync(null); MacHost.Pump();
            Assert.False(state.ShowInstallUpdate); Assert.Equal(loc["Upd_Unverified"], state.UpdateStatusText); Save(panel, $"update-unverified-{language}-{dark}");
            updates.CheckError = null; updates.Status = "no_release"; await state.CheckUpdatesCommand.ExecuteAsync(null); MacHost.Pump();
            Assert.Equal(loc["Upd_NoRelease"], state.UpdateStatusText); Save(panel, $"update-no-release-{language}-{dark}");
            window.Width = 980; MacHost.Pump(); MacHost.Save(window, $"update-settings-compact-{language}-{dark}");
        }
        finally { window.Close(); loc.Apply(original); theme.Apply(AppThemeMode.System); Directory.Delete(directory, true); }
    }
    [AvaloniaFact]
    public void Development_build_without_source_disables_checking_and_explains_why()
    {
        MacHost.EnsureComposed(); var loc = AppServices.Get<LocalizationService>();
        var source = new FakeUpdates { IsConfigured = false };
        var vm = new SettingsViewModel(new(MacHost.DataDir), loc, AppServices.Get<MacTheme>(), AppServices.DataDirectory, AppServices.Provider, updater: source);
        var window = new MainWindow();
        try
        {
            window.Show(); window.Width = 1920; window.Height = 1080; window.Navigate(AppPage.Settings);
            var page = (SettingsPage)window.CurrentView!; page.DataContext = vm; MacHost.Pump();
            var panel = page.FindControl<Border>("UpdatePanel")!;
            var check = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.Updates.CheckUpdatesCommand);
            Assert.False(check.IsEffectivelyEnabled); Assert.False(check.Command!.CanExecute(null)); Assert.Equal(loc["Upd_Disabled"], vm.Updates.UpdateStatusText); Assert.Equal(0, source.Checks);
            MacHost.Save(window, "updates-development-unconfigured-1080p");
        }
        finally { window.AllowClose(); window.Close(); vm.Updates.Dispose(); }
    }
    private static void Save(Control control, string name)
    {
        if (Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height)));
        frame.Render(control); frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }
}
