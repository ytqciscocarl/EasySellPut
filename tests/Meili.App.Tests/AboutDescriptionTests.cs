using System.Windows.Controls;
using Meili.App.Services;
using Meili.App.Views.Pages;
namespace Meili.App.Tests;
public sealed class AboutDescriptionTests
{
    [Theory]
    [InlineData("zh-CN", false)]
    [InlineData("en-US", true)]
    public void About_source_information_is_readable_without_hover_or_click(string language, bool dark)
    {
        WpfHost.Run(async () =>
        {
            ScreenRenderTests.Apply(language, dark); var w = new MainWindow { Width = 1920, Height = 1080 };
            try
            {
                w.Show(); w.NavigateForSmoke(typeof(SettingsPage)); await ScreenRenderTests.Settle();
                var page = UiAudit.Descendants<SettingsPage>(w).Single(); var text = (TextBlock)page.FindName("AboutDescription");
                text.BringIntoView(); await ScreenRenderTests.Settle();
                Assert.Equal(AppServices.Get<LocalizationService>()["Set_AboutDesc"], text.Text); Assert.True(text.IsVisible);
                Assert.True(text.ActualHeight > text.FontSize * 2);
                w.Width = 980; text.BringIntoView(); await ScreenRenderTests.Settle();
                Assert.InRange(text.ActualWidth, 100, 980); Assert.True(text.ActualHeight > text.FontSize * 2);
            }
            finally { w.Close(); }
        });
    }
}
