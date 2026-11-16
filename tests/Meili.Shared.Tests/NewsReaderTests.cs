using Meili.Core.Settings;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Tests;

[Collection(nameof(UiPlatformCollection))]
public sealed class NewsReaderTests
{
    [Theory]
    [InlineData("https://example.com/news?id=1", true)]
    [InlineData("http://127.0.0.1:1234/article", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("https://user:password@example.com/article", false)]
    [InlineData("/relative", false)]
    [InlineData(null, false)]
    public void Reader_accepts_only_web_urls_without_credentials(string? url, bool allowed) =>
        Assert.Equal(allowed, NewsReaderPolicy.IsAllowed(url));

    [Fact]
    public void News_uses_internal_reader_while_company_website_keeps_explicit_browser_behavior()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meili-news-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ui = new FakeUiPlatform(); Ui.Platform = ui;
            var loc = new LocalizationService();
            var vm = new NewsViewModel(new WatchlistStore(new SettingsStore(directory)),
                new PricingSnapshotTests.SnapshotData(), loc, new SymbolContext());
            vm.OpenArticleCommand.Execute(new NewsEntry { Url = "https://example.com/article", Title = "Article title" });
            Assert.Equal(("https://example.com/article", "Article title"), Assert.Single(ui.Articles));
            Assert.Empty(ui.Opened);
            vm.OpenArticleCommand.Execute(new NewsEntry { Url = "file:///private/file", Title = "Blocked" });
            Assert.Single(ui.Articles);
            Assert.Equal(loc["Reader_Blocked"], vm.StatusText);
            vm.WebsiteUrl = "https://example.com";
            vm.OpenWebsiteCommand.Execute(null);
            Assert.Equal("https://example.com", Assert.Single(ui.Opened));
            vm.Deactivate();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
