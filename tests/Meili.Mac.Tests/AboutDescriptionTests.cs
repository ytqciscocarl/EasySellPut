using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views;
namespace Meili.Mac.Tests;
public sealed class AboutDescriptionTests
{
    [AvaloniaTheory]
    [InlineData("zh-CN", false)]
    [InlineData("en-US", true)]
    public void About_source_and_usage_information_is_visible_without_opening_help(string language, bool dark)
    {
        MacHost.EnsureComposed(); var loc = AppServices.Get<LocalizationService>(); var original = loc.Language;
        var theme = AppServices.Get<MacTheme>(); loc.Apply(language); theme.Apply(dark ? AppThemeMode.Dark : AppThemeMode.Light);
        var w = new MainWindow();
        try
        {
            w.Show(); w.Width = 1920; w.Height = 1080; w.Navigate(AppPage.Settings); MacHost.Pump();
            var description = ((SettingsPage)w.CurrentView!).FindControl<TextBlock>("AboutDescription")!;
            description.BringIntoView(); MacHost.Pump();
            Assert.Equal(loc["Set_AboutDesc"], description.Text); Assert.True(description.IsEffectivelyVisible);
            Assert.True(description.Bounds.Height > description.FontSize * 2);
            Assert.InRange(description.TranslatePoint(default, w)!.Value.Y, 0, w.Bounds.Height - description.Bounds.Height);
            MacHost.Save(w, $"about-visible-1080p-{language}-{dark}");
            w.Width = 980; description.BringIntoView(); MacHost.Pump();
            Assert.InRange(description.Bounds.Width, 100, 980); Assert.True(description.Bounds.Height > description.FontSize * 2);
        }
        finally { w.AllowClose(); w.Close(); loc.Apply(original); theme.Apply(AppThemeMode.System); }
    }
}
