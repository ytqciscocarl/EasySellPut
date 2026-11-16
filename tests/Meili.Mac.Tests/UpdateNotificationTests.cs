using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views;
using Meili.Shared.Tests;

namespace Meili.Mac.Tests;
public sealed class UpdateNotificationTests
{
    [AvaloniaTheory]
    [InlineData("zh-CN", false)]
    [InlineData("en-US", true)]
    public async Task Footer_only_appears_for_updates_and_later_keeps_a_reopenable_shared_dialog(string language, bool dark)
    {
        MacHost.EnsureComposed(); var loc = AppServices.Get<LocalizationService>(); var original = loc.Language;
        var theme = AppServices.Get<MacTheme>(); loc.Apply(language); theme.Apply(dark ? AppThemeMode.Dark : AppThemeMode.Light);
        var source = new FakeUpdates();
        using var state = new UpdateViewModel(new(MacHost.DataDir), loc, AppServices.DataDirectory, source);
        var w = new MainWindow();
        try
        {
            w.UseUpdateState(state); w.Show(); w.Width = 1920; w.Height = 1080; MacHost.Pump();
            var badge = w.FindControl<Button>("UpdateStatusIndicator")!;
            Assert.False(badge.IsVisible); Assert.Empty(w.OwnedWindows.OfType<UpdateDialog>());
            await state.CheckUpdatesCommand.ExecuteAsync(null); MacHost.Pump();
            Assert.True(badge.IsVisible); Assert.Same(state, badge.DataContext);
            var dialog = Assert.Single(w.OwnedWindows.OfType<UpdateDialog>());
            Assert.Same(state, dialog.DataContext); Assert.True(dialog.IsVisible);
            Assert.Equal(0, source.Downloads); Assert.Equal(0, source.Installs);
            MacHost.Save(w, $"update-notification-footer-{language}-{dark}");
            MacHost.Save(dialog, $"update-notification-dialog-{language}-{dark}");
            dialog.FindControl<Button>("LaterButton")!.Command!.Execute(null); MacHost.Pump();
            Assert.False(state.IsUpdatePromptOpen); Assert.True(badge.IsVisible); Assert.Empty(w.OwnedWindows.OfType<UpdateDialog>());
            badge.Command!.Execute(null); MacHost.Pump(); Assert.Single(w.OwnedWindows.OfType<UpdateDialog>());
            await state.DownloadUpdateCommand.ExecuteAsync(null); MacHost.Pump();
            Assert.True(state.ShowInstallUpdate); Assert.Equal(1, source.Downloads); Assert.Equal(0, source.Installs);
            Assert.Contains("0.7.0", state.UpdateBadgeText);
            w.Hide(); MacHost.Pump(); Assert.False(state.IsUpdatePromptOpen); Assert.Empty(w.OwnedWindows.OfType<UpdateDialog>());
            w.Show(); MacHost.Pump(); Assert.True(badge.IsVisible); Assert.Empty(w.OwnedWindows.OfType<UpdateDialog>());
            source.Status = "current"; await state.CheckUpdatesCommand.ExecuteAsync(null); MacHost.Pump();
            Assert.False(badge.IsVisible);
        }
        finally { w.AllowClose(); w.Close(); loc.Apply(original); theme.Apply(AppThemeMode.System); }
    }
}
