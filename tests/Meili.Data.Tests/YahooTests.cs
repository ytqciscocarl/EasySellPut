using System.Net;
using Meili.Core.Models;
using Meili.Data.Http;
using Meili.Data.Providers.Yahoo;

namespace Meili.Data.Tests;

public class YahooTests
{
    [Fact]
    public void Parses_batch_quotes_with_premarket_and_symbol_mapping()
    {
        using var d = Fx.Json("yahoo_quote.json");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BRK-B"] = "BRK.B" };
        var q = YahooProvider.ParseQuotes(d.RootElement, map);
        Assert.Equal(["AAPL", "SPY", "TSLA", "BRK.B"], q.Select(x => x.Symbol));
        var aapl = q[0];
        Assert.Equal(350.69, aapl.Price, 2);
        Assert.Equal(MarketState.PreMarket, aapl.MarketState);
        Assert.Equal(351.7301, aapl.ExtendedPrice!.Value, 4);
        Assert.True(aapl.IsRealtime);
        Assert.Equal("yahoo", aapl.Source);
        Assert.NotNull(aapl.Name);
        Assert.All(q, x => Assert.True(x.PreviousClose > 0 && x.AsOf is not null));
    }

    private static IReadOnlyList<Quote> ParseInline(string json)
    {
        using var d = System.Text.Json.JsonDocument.Parse(json);
        return YahooProvider.ParseQuotes(d.RootElement);
    }

    [Fact]
    public void Overnight_PREPRE_without_premarket_price_takes_the_postmarket_fields()
    {
        var q = ParseInline("""
            {"quoteResponse":{"result":[{"symbol":"AAPL","marketState":"PREPRE","regularMarketPrice":100,
            "postMarketPrice":94,"postMarketChange":-6,"postMarketChangePercent":-6.0,"postMarketTime":1700000000}]}}
            """);
        var a = Assert.Single(q);
        Assert.Equal(MarketState.PreMarket, a.MarketState);
        Assert.Equal(94, a.ExtendedPrice);
        Assert.Equal(-6, a.ExtendedChange);
        Assert.Equal(-6.0, a.ExtendedChangePercent);
        Assert.NotNull(a.ExtendedTime);
    }

    [Fact]
    public void Premarket_price_wins_over_postmarket_in_PRE()
    {
        var q = ParseInline("""
            {"quoteResponse":{"result":[{"symbol":"AAPL","marketState":"PRE","regularMarketPrice":100,
            "preMarketPrice":101.5,"postMarketPrice":94}]}}
            """);
        Assert.Equal(101.5, Assert.Single(q).ExtendedPrice);
    }

    [Fact]
    public void Regular_session_never_reports_an_extended_price()
    {
        var q = ParseInline("""
            {"quoteResponse":{"result":[{"symbol":"AAPL","marketState":"REGULAR","regularMarketPrice":100,
            "preMarketPrice":99,"postMarketPrice":94}]}}
            """);
        Assert.Null(Assert.Single(q).ExtendedPrice);
    }

    [Fact]
    public void Parses_intraday_chart_including_extended_hours()
    {
        using var d = Fx.Json("yahoo_chart_1m.json");
        var s = YahooProvider.ParseChart(d.RootElement, "AAPL", BarInterval.Minute1);
        Assert.Equal(126, s.Bars.Count);
        Assert.All(s.Bars, b => Assert.True(b.Low <= b.Open && b.Low <= b.Close && b.High >= b.Open && b.High >= b.Close));
        Assert.True(s.Bars.Zip(s.Bars.Skip(1)).All(p => p.First.Time < p.Second.Time));
        // first bar is pre-market (before 09:30 New York)
        Assert.True(Meili.Core.Time.MarketClock.ToEastern(s.Bars[0].Time).TimeOfDay < new TimeSpan(9, 30, 0));
    }

    [Fact]
    public void Parses_daily_chart()
    {
        using var d = Fx.Json("yahoo_chart_1d.json");
        var s = YahooProvider.ParseChart(d.RootElement, "SPY", BarInterval.Day1);
        Assert.Equal(65, s.Bars.Count);
        Assert.Equal(752.69, s.Bars[^1].Close, 2);
    }

    [Fact]
    public void Parses_option_chain_and_drops_placeholder_iv()
    {
        using var d = Fx.Json("yahoo_options.json");
        var c = YahooProvider.ParseOptions(d.RootElement, "AAPL");
        Assert.Equal(22, c.Expirations.Count);
        Assert.Equal(45, c.Contracts.Count(x => x.Right == OptionRight.Call));
        Assert.Equal(39, c.Contracts.Count(x => x.Right == OptionRight.Put));
        Assert.Equal(350.69, c.UnderlyingPrice!.Value, 2);
        Assert.Equal(15, c.DelayMinutes);
        Assert.All(c.Contracts, x => Assert.Equal(c.Expiration, x.Expiration));
        Assert.All(c.Contracts, x => Assert.True(x.ImpliedVolatility is null or >= 0.001));
        Assert.Contains(c.Contracts, x => x.OpenInterest > 0);
    }

    [Fact]
    public void Parses_quote_summary_fundamentals()
    {
        using var d = Fx.Json("yahoo_quotesummary.json");
        var f = YahooProvider.ParseQuoteSummary(d.RootElement, "AAPL")!;
        Assert.Equal("Apple Inc.", f.Name);
        Assert.Equal("Technology", f.Sector);
        Assert.Equal(39.05, f.TrailingPE!.Value, 2);
        Assert.True(f.MarketCap > 1e12);
        Assert.False(string.IsNullOrEmpty(f.Description));
    }

    [Fact]
    public void Parses_financial_timeseries()
    {
        using var d = Fx.Json("yahoo_timeseries.json");
        var fs = YahooProvider.ParseTimeseries(d.RootElement, "AAPL");
        Assert.Equal(44, fs.Lines.Count);
        Assert.Contains(fs.Lines, l => l.Item == FinancialItems.TotalRevenue && l.Value > 3e11);
        Assert.Equal(11, fs.Lines.Select(l => l.Item).Distinct().Count());
    }

    [Fact]
    public void Parses_search_and_news()
    {
        using var d = Fx.Json("yahoo_search.json");
        var m = YahooProvider.ParseSearch(d.RootElement);
        Assert.Equal(7, m.Count);
        Assert.Equal("AAPL", m[0].Symbol);
        var n = YahooProvider.ParseNews(d.RootElement);
        Assert.Equal(8, n.Count);
        Assert.All(n, x => Assert.StartsWith("http", x.Url));
    }

    [Fact]
    public async Task Obtains_crumb_then_refreshes_it_on_401()
    {
        var h = new FakeHandler()
            .Enqueue(HttpStatusCode.NotFound, "")           // fc.yahoo.com cookie
            .Enqueue(HttpStatusCode.OK, "crumbA")           // getcrumb
            .Enqueue(HttpStatusCode.Unauthorized, "")       // quote with stale crumb
            .Enqueue(HttpStatusCode.NotFound, "")           // cookie again
            .Enqueue(HttpStatusCode.OK, "crumbB")
            .Enqueue(_ => FakeHandler.Resp(HttpStatusCode.OK, Fx.Bytes("yahoo_quote.json")));
        var y = new YahooProvider(TestHttp.Gateway(h), new ResponseCache());
        var q = await y.GetQuotesAsync(["aapl", "AAPL", "brk.b"]);
        Assert.Equal(4, q.Count);
        Assert.Contains(q, x => x.Symbol == "BRK.B");
        Assert.Contains("crumb=crumbB", h.Requests[^1].RequestUri!.Query);
        Assert.Contains("symbols=AAPL%2CBRK-B", h.Requests[^1].RequestUri!.Query);
    }
}