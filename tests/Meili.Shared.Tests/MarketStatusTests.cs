using Meili.Core.Settings;
using Meili.Data;
using Meili.Shared.ViewModels;
namespace Meili.Shared.Tests;
[Collection(nameof(UiPlatformCollection))]
public sealed class MarketStatusTests
{
    [Fact]
    public void Combined_status_keeps_market_and_source_health_independent_and_updates_details_on_restore()
    {
        Ui.Platform = new FakeUiPlatform(); var loc = new LocalizationService();
        using var data = new DataServices(new());
        using var shell = new ShellViewModel(loc, new(Path.Combine(Path.GetTempPath(), "market-status-" + Guid.NewGuid().ToString("N")), DataDirectorySource.CommandLine), data);
        var total = data.Health.All.Count; var id = data.Health.All[0].Id;
        Assert.Equal(shell.SessionText + " · 0/" + total, shell.MarketStatusText);
        Assert.Equal("UnknownBrush", shell.SourcesBrush);
        Assert.Contains(shell.SourcesText, shell.MarketStatusTip);
        shell.NetworkDescription = "local proxy"; Assert.Contains(shell.NetworkText, shell.MarketStatusTip);
        data.Health.RecordSuccess(id, TimeSpan.FromMilliseconds(10)); shell.SetPresentationVisible(true);
        Assert.EndsWith("1/" + total, shell.MarketStatusText); Assert.Equal("HealthyBrush", shell.SourcesBrush);
        data.Health.RecordFailure(id, "fixture error"); shell.SetPresentationVisible(false);
        Assert.EndsWith("1/" + total, shell.MarketStatusText);
        shell.SetPresentationVisible(true);
        Assert.EndsWith("0/" + total, shell.MarketStatusText); Assert.Equal("DegradedBrush", shell.SourcesBrush);
        loc.Apply("en-US"); Assert.Contains(shell.SessionText, shell.MarketStatusText); Assert.Contains(shell.SourcesText, shell.MarketStatusTip);
        Assert.DoesNotContain(shell.SourcesText, shell.MarketStatusText);
    }
}
