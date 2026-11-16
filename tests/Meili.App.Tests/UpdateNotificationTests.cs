using System.IO;
using System.Windows;
using System.Windows.Controls;
using Meili.App.Services;
using Meili.App.Views;
using Meili.Core.Settings;
using Meili.Shared.Tests;
namespace Meili.App.Tests;
public sealed class UpdateNotificationTests
{
    [Theory]
    [InlineData("zh-CN", false)]
    [InlineData("en-US", true)]
    public void Footer_and_owned_reminder_share_state_and_do_not_download_without_an_explicit_action(string language, bool dark)
    {
        WpfHost.Run(async () =>
        {
            ScreenRenderTests.Apply(language, dark);
            var source = new FakeUpdates();
            using var state = new UpdateViewModel(new(WpfHost.DataDir), AppServices.Get<LocalizationService>(), AppServices.DataDirectory, source);
            var w = new MainWindow { Width = 1200, Height = 760 };
            try
            {
                w.UseUpdateState(state); w.Show(); await ScreenRenderTests.Settle();
                var badge = (Button)w.FindName("UpdateStatusIndicator");
                Assert.Equal(Visibility.Collapsed, badge.Visibility);
                await state.CheckUpdatesCommand.ExecuteAsync(null); await ScreenRenderTests.Settle();
                Assert.Equal(Visibility.Visible, badge.Visibility); Assert.Same(state, badge.DataContext);
                var dialog = Assert.Single(w.OwnedWindows.OfType<UpdateDialog>());
                Assert.Same(state, dialog.DataContext); Assert.True(dialog.IsVisible);
                Assert.Equal(0, source.Downloads); Assert.Equal(0, source.Installs);
                if (Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is { Length: > 0 } dir)
                {
                    Directory.CreateDirectory(dir); dialog.Activate(); await ScreenRenderTests.Settle();
                    NewsReaderFixture.Capture(dialog, Path.Combine(dir, $"update-notification-full-desktop-{language}-{dark}.png"), entireDesktop: true);
                }
                ((Button)dialog.FindName("LaterButton")).Command!.Execute(null); await WpfHost.Settle();
                Assert.False(state.IsUpdatePromptOpen); Assert.Equal(Visibility.Visible, badge.Visibility); Assert.Empty(w.OwnedWindows.OfType<UpdateDialog>());
                badge.Command!.Execute(null); await WpfHost.Settle(); Assert.Single(w.OwnedWindows.OfType<UpdateDialog>());
                await state.DownloadUpdateCommand.ExecuteAsync(null); await WpfHost.Settle();
                Assert.True(state.ShowInstallUpdate); Assert.Equal(0, source.Installs);
                w.Hide(); await WpfHost.Settle(); Assert.False(state.IsUpdatePromptOpen); Assert.Empty(w.OwnedWindows.OfType<UpdateDialog>());
                w.Show(); await WpfHost.Settle(); Assert.Empty(w.OwnedWindows.OfType<UpdateDialog>());
                source.Status = "current"; await state.CheckUpdatesCommand.ExecuteAsync(null); await WpfHost.Settle(); Assert.Equal(Visibility.Collapsed, badge.Visibility);
            }
            finally { w.Close(); }
        });
    }
}
