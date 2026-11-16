using Meili.Automation;

namespace Meili.Automation.Tests;

public sealed class MarketTimelineTests
{
    [Fact]
    public async Task Dates_precision_and_confirmation_are_preserved_across_components()
    {
        var result = await MarketTimelineService.LoadAsync(new FakeMarket(), "aapl");
        Assert.Equal("AAPL", result.Symbol);
        Assert.Equal("complete", result.Status);
        var news = Assert.Single(result.Events,e => e.Type == "news");
        Assert.Equal("timestamp", news.Precision);
        Assert.Equal(new DateOnly(2026,11,13), news.Date);
        Assert.Null(news.ProviderAsOf); // receipt time does not masquerade as provider freshness
        var earnings = Assert.Single(result.Events,e => e.Type == "earnings");
        Assert.Null(earnings.Published);
        Assert.Equal("date", earnings.Precision);
        Assert.Equal("estimated", earnings.Confirmation);
        Assert.Equal("unverified", Assert.Single(result.Events,e => e.Type == "ex_dividend").Confirmation);
        var repeated = await MarketTimelineService.LoadAsync(new FakeMarket(), "AAPL");
        Assert.Equal(result.Events.Select(e => e.Id), repeated.Events.Select(e => e.Id));
    }
    [Fact]
    public async Task News_date_filters_use_Eastern_date_rather_than_UTC_midnight()
    {
        var result=await MarketTimelineService.LoadAsync(new FakeMarket{Publication=new(2026,11,14,1,0,0,TimeSpan.Zero)},"AAPL","news",new(2026,11,13),new(2026,11,13));
        Assert.Equal(new DateOnly(2026,11,13),Assert.Single(result.Events).Date);
    }
    [Fact]
    public async Task Partial_failure_is_explicit_and_other_events_survive()
    {
        var result = await MarketTimelineService.LoadAsync(new FakeMarket { FailNews = true }, "AAPL");
        Assert.Equal("partial", result.Status); Assert.Equal(2, result.Events.Count);
        Assert.Equal("unavailable", result.Coverage.Single(c => c.Type == "news").Status);
        var missing = await MarketTimelineService.LoadAsync(new FakeMarket { FailNews = true, FailFundamentals = true }, "AAPL");
        Assert.Equal("unavailable", missing.Status); Assert.Empty(missing.Events);
    }
    [Fact]
    public async Task Date_filters_exclude_undated_news_without_inventing_a_time()
    {
        var all = await MarketTimelineService.LoadAsync(new FakeMarket { UnknownPublication = true }, "AAPL", "news");
        Assert.Null(Assert.Single(all.Events).Date); Assert.Equal("unknown", all.Events[0].Precision);
        var filtered = await MarketTimelineService.LoadAsync(new FakeMarket { UnknownPublication = true }, "AAPL", "news", new(2026,11,1));
        Assert.Empty(filtered.Events); Assert.Equal(1, filtered.UndatedExcluded);
        var bounded = await MarketTimelineService.LoadAsync(new FakeMarket(), "AAPL", limit:1);
        Assert.Single(bounded.Events); Assert.True(bounded.Truncated);
    }
    [Fact]
    public async Task Invalid_filters_and_cancellation_do_not_become_successful_empty_results()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => MarketTimelineService.LoadAsync(new FakeMarket(),"AAPL", "bad"));
        await Assert.ThrowsAsync<ArgumentException>(() => MarketTimelineService.LoadAsync(new FakeMarket(),"AAPL", from:new(2026,12,1),to:new(2026,11,1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MarketTimelineService.LoadAsync(new FakeMarket(),"AAPL",ct:new CancellationToken(true)));
    }
    [Fact]
    public async Task A_provider_ignoring_cancellation_cannot_keep_a_timeline_request_alive()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<Meili.Core.Models.NewsItem>>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>MarketTimelineService.LoadAsync(new FakeMarket{PendingNews=pending.Task},"AAPL",ct:cancellation.Token));
    }
    [Theory]
    [InlineData("https://example.com/a",true)]
    [InlineData("file:///etc/passwd",false)]
    [InlineData("javascript:alert(1)",false)]
    [InlineData("https://name:secret@example.com",false)]
    public void News_links_are_http_without_credentials(string url,bool allowed) => Assert.Equal(allowed,MarketTimelineService.IsSafeUrl(url));
}
