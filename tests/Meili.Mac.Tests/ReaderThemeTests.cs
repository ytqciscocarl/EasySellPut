using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Shared.Services;

namespace Meili.Mac.Tests;

public class ReaderThemeTests
{
    [AvaloniaFact]
    public void Reader_chrome_tracks_live_theme_and_language_without_opening_a_remote_site()
    {
        MacHost.EnsureComposed();
        var theme = AppServices.Get<MacTheme>();
        var loc = LocalizationService.Current;
        var reader = new NewsReaderWindow();
        try
        {
            reader.Show(); MacHost.Pump();
            var root = (DockPanel)reader.Content!;
            var toolbar = root.Children.OfType<WrapPanel>().Single();
            var status = root.Children.OfType<TextBlock>().Single();
            Assert.False(status.IsVisible);
            Assert.All(toolbar.Children.OfType<Button>().Take(2), button => Assert.False(button.IsEnabled));
            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                theme.Apply(mode); MacHost.Pump();
                var palette = ThemePalette.For(mode == AppThemeMode.Dark);
                Assert.Equal(Color.FromUInt32(palette.Window), ((ISolidColorBrush)reader.Background!).Color);
                Assert.Equal(Color.FromUInt32(palette.PrimaryText), ((ISolidColorBrush)reader.Foreground!).Color);
                reader.OpenArticle("file:///blocked", "Blocked"); MacHost.Pump();
                Assert.True(status.IsVisible);
                Assert.Equal(Color.FromUInt32(palette.ErrorBar), ((ISolidColorBrush)status.Background!).Color);
                Assert.All(toolbar.Children.OfType<Button>(), button => Assert.True(button.MinHeight >= 32 && button.FontSize >= 14));
                MacHost.Save(reader, "reader-" + mode);
            }
            loc.Apply("en-US"); MacHost.Pump();
            Assert.Equal(loc["Reader_Blocked"], status.Text);
            Assert.Equal(loc["Reader_Back"], toolbar.Children.OfType<Button>().First().Content);
        }
        finally { reader.Close(); loc.Apply("zh-CN"); theme.Apply(AppThemeMode.Dark); }
    }
}
