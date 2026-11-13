using System.Text.Json;
using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Data.Http;
using Meili.Data.Json;

namespace Meili.Data.Providers.Yahoo;

/// <summary>
/// Yahoo Finance public JSON endpoints (no account). Quotes/options/quoteSummary need a session cookie + "crumb":
/// GET https://fc.yahoo.com (sets cookie, 404 is normal) then GET /v1/test/getcrumb.
/// </summary>
public sealed class YahooProvider : ProviderBase, IQuoteProvider, IBarsProvider, IOptionsProvider, IFundamentalsProvider,
    IFinancialsProvider, INewsProvider, ISymbolSearchProvider
{
    private const string Q1 = "https://query1.finance.yahoo.com";
    private const string Q2 = "https://query2.finance.yahoo.com";
    private readonly SemaphoreSlim _crumbLock = new(1, 1);
    private string? _crumb;
    private DateTimeOffset _crumbAt;

    public YahooProvider(HttpGateway http, ResponseCache cache) : base(http, cache) { }

    public override string Id => "yahoo";
    public override string DisplayName => "Yahoo Finance";
    public override DataCapability Capabilities => DataCapability.Quote | DataCapability.IntradayBars | DataCapability.DailyBars |
        DataCapability.Options | DataCapability.Fundamentals | DataCapability.Financials | DataCapability.News | DataCapability.Search;
    public override TimeSpan MinRequestSpacing => TimeSpan.FromMilliseconds(250);

    /// <summary>Yahoo uses ^ for indices (e.g. ^GSPC, ^SPX) and "-" for share classes (BRK-B).</summary>
    internal static string ToYahoo(string symbol) => Norm(symbol).Replace('.', '-').Replace('/', '-');

    // ---------------- crumb ----------------

    internal async Task<string> GetCrumbAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _crumb is not null && DateTimeOffset.UtcNow - _crumbAt < TimeSpan.FromHours(6)) return _crumb;
        await _crumbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _crumb is not null && DateTimeOffset.UtcNow - _crumbAt < TimeSpan.FromHours(6)) return _crumb;
            await GetAsync("https://fc.yahoo.com/", ct, new Dictionary<string, string> { ["Accept"] = "text/html" }, acceptStatus: _ => true)
                .ConfigureAwait(false);
            foreach (var host in new[] { Q2, Q1 })
            {
                try
                {
                    var p = await GetAsync($"{host}/v1/test/getcrumb", ct, new Dictionary<string, string> { ["Accept"] = "text/plain" }).ConfigureAwait(false);
                    var c = p.Text().Trim();
                    if (c.Length is > 0 and < 64 && !c.StartsWith('{') && !c.StartsWith('<'))
                    {
                        _crumb = c; _crumbAt = DateTimeOffset.UtcNow;
                        return c;
                    }
                }
                catch (DataSourceException) when (host == Q2) { }
            }
            throw new DataSourceException(Id, DataErrorKind.Blocked, "could not obtain crumb");
        }
        finally { _crumbLock.Release(); }
    }

    /// <summary>GET a crumb-protected endpoint; on 401/403 (stale crumb) refresh once and retry.</summary>
    private async Task<JsonDocument> GetWithCrumbAsync(Func<string, string> url, CancellationToken ct)
    {
        var crumb = await GetCrumbAsync(ct).ConfigureAwait(false);
        try { return await GetJsonAsync(url(crumb), ct).ConfigureAwait(false); }
        catch (DataSourceException ex) when (ex.StatusCode is 401 or 403)
        {
            crumb = await GetCrumbAsync(ct, forceRefresh: true).ConfigureAwait(false);
            return await GetJsonAsync(url(crumb), ct).ConfigureAwait(false);
        }
    }

    // ---------------- quotes ----------------

    public async Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        if (symbols.Count == 0) return [];
        var list = string.Join(",", symbols.Select(ToYahoo).Distinct());
        using var doc = await GetWithCrumbAsync(c => $"{Q2}/v7/finance/quote?symbols={Enc(list)}&crumb={Enc(c)}", ct).ConfigureAwait(false);
        var back = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in symbols) back.TryAdd(ToYahoo(s), Norm(s));
        return ParseQuotes(doc.RootElement, back);
    }

    internal static IReadOnlyList<Quote> ParseQuotes(JsonElement root, IReadOnlyDictionary<string, string>? symbolMap = null)
    {
        var result = new List<Quote>();
        foreach (var q in root.Path("quoteResponse", "result").Items())
        {
            var ysym = q.Str("symbol");
            var price = q.Num("regularMarketPrice");
            if (ysym is null || price is null or <= 0) continue;
            var state = (q.Str("marketState") ?? "").ToUpperInvariant() switch
            {
                "PRE" or "PREPRE" => MarketState.PreMarket,
                "REGULAR" => MarketState.Regular,
                "POST" or "POSTPOST" => MarketState.AfterHours,
                "CLOSED" => MarketState.Closed,
                _ => MarketState.Unknown,
            };
            double? extP = null, extC = null, extPct = null; DateTimeOffset? extT = null;
            // The pre-market price wins in PreMarket; with none yet (Yahoo PREPRE, overnight) the latest post-market trade is the most recent.
            if (state == MarketState.PreMarket && q.Num("preMarketPrice") is > 0 and { } pre)
            { extP = pre; extC = q.Num("preMarketChange"); extPct = q.Num("preMarketChangePercent"); extT = q.UnixSeconds("preMarketTime"); }
            else if (state != MarketState.Regular && q.Num("postMarketPrice") is > 0 and { } post)
            { extP = post; extC = q.Num("postMarketChange"); extPct = q.Num("postMarketChangePercent"); extT = q.UnixSeconds("postMarketTime"); }

            // F6: trust only what Yahoo states — the exchange delay field or a "Real Time" quote source.
            var declared = q.Num("exchangeDataDelayedBy") is { } dd ? (int?)(int)dd : null;
            var realtime = (q.Str("quoteSourceName") ?? "").Contains("Real Time", StringComparison.OrdinalIgnoreCase) || declared == 0;
            int? delay = realtime ? 0 : declared is > 0 ? declared : null;
            result.Add(new Quote
            {
                Symbol = symbolMap is not null && symbolMap.TryGetValue(ysym, out var orig) ? orig : ysym,
                Name = q.Str("longName") ?? q.Str("shortName"),
                Exchange = q.Str("fullExchangeName") ?? q.Str("exchange"),
                Currency = q.Str("currency") ?? "USD",
                Price = price.Value,
                Change = q.Num("regularMarketChange"),
                ChangePercent = q.Num("regularMarketChangePercent"),
                PreviousClose = q.Num("regularMarketPreviousClose"),
                Open = q.Num("regularMarketOpen"),
                High = q.Num("regularMarketDayHigh"),
                Low = q.Num("regularMarketDayLow"),
                Volume = q.Num("regularMarketVolume"),
                Bid = Positive(q.Num("bid")),
                Ask = Positive(q.Num("ask")),
                BidSize = q.Num("bidSize"),
                AskSize = q.Num("askSize"),
                FiftyTwoWeekHigh = q.Num("fiftyTwoWeekHigh"),
                FiftyTwoWeekLow = q.Num("fiftyTwoWeekLow"),
                MarketCap = q.Num("marketCap"),
                MarketState = state,
                ExtendedPrice = extP,
                ExtendedChange = extC,
                ExtendedChangePercent = extPct,
                ExtendedTime = extT,
                AsOf = q.UnixSeconds("regularMarketTime"),
                IsRealtime = realtime,
                DelayMinutes = delay,
                Source = "yahoo",
            });
        }
        return result;
    }

    private static double? Positive(double? v) => v is > 0 ? v : null;

    // ---------------- bars ----------------

    public bool Supports(BarInterval interval) => true;

    public async Task<BarSeries> GetBarsAsync(BarRequest r, CancellationToken ct = default)
    {
        var (iv, defRange) = r.Interval switch
        {
            BarInterval.Minute1 => ("1m", "5d"),
            BarInterval.Minute5 => ("5m", "1mo"),
            BarInterval.Minute15 => ("15m", "1mo"),
            BarInterval.Minute30 => ("30m", "1mo"),
            BarInterval.Hour1 => ("60m", "6mo"),
            BarInterval.Day1 => ("1d", "2y"),
            BarInterval.Week1 => ("1wk", "10y"),
            _ => ("1mo", "max"),
        };
        var span = r.From is { } from
            ? $"period1={from.ToUnixTimeSeconds()}&period2={(r.To ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}"
            : $"range={defRange}";
        var pre = r.Interval.IsIntraday() ? "&includePrePost=true" : "";
        var url = $"{Q1}/v8/finance/chart/{Enc(ToYahoo(r.Symbol))}?interval={iv}&{span}{pre}&events=div%2Csplits";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var series = ParseChart(doc.RootElement, Norm(r.Symbol), r.Interval, r.Adjust);
        if (series.Bars.Count == 0) throw NoData($"no bars for {r.Symbol}");
        return series;
    }

    /// <remarks>
    /// F7: Yahoo's OHLC are split-adjusted but not dividend-adjusted. For <see cref="AdjustMode.Total"/> on daily+
    /// bars, every OHLC is scaled by adjclose/close (the standard total-return adjustment).
    /// </remarks>
    internal static BarSeries ParseChart(JsonElement root, string symbol, BarInterval interval, AdjustMode adjust = AdjustMode.Split)
    {
        var res = root.Path("chart", "result")?.At(0);
        var bars = new List<Bar>();
        var basis = PriceAdjustment.Split;
        DateTimeOffset? asOf = null;
        if (res is { } r)
        {
            asOf = r.Prop("meta") is { } meta ? meta.UnixSeconds("regularMarketTime") : null;
            var ts = r.Prop("timestamp").Items().ToArray();
            var q = r.Path("indicators", "quote")?.At(0);
            if (q is { } quote)
            {
                var o = quote.Prop("open").Items().ToArray();
                var h = quote.Prop("high").Items().ToArray();
                var l = quote.Prop("low").Items().ToArray();
                var c = quote.Prop("close").Items().ToArray();
                var v = quote.Prop("volume").Items().ToArray();
                var adj = adjust == AdjustMode.Total && !interval.IsIntraday()
                    ? r.Path("indicators", "adjclose")?.At(0)?.Prop("adjclose").Items().ToArray()
                    : null;
                if (adj is { Length: > 0 }) basis = PriceAdjustment.SplitDividend;
                for (var i = 0; i < ts.Length; i++)
                {
                    if (i >= c.Length || c[i].Num() is not { } close) continue;
                    var f = 1.0;
                    if (adj is { Length: > 0 })
                    {
                        if (i >= adj.Length || adj[i].Num() is not { } ac || !(close > 0)) continue; // no adjusted value: skip, never mix bases
                        f = ac / close;
                    }
                    double? S(double? x) => x * f;
                    bars.Add(Bar.FromSource(DateTimeOffset.FromUnixTimeSeconds((long)ts[i].GetDouble()),
                        S(i < o.Length ? o[i].Num() : null), S(i < h.Length ? h[i].Num() : null),
                        S(i < l.Length ? l[i].Num() : null), close * f, i < v.Length ? v[i].Num() : null));
                }
            }
        }
        return new BarSeries { Symbol = symbol, Interval = interval, Bars = bars, Source = "yahoo", AsOf = asOf, Adjustment = basis };
    }

    // ---------------- options ----------------

    public async Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string symbol, CancellationToken ct = default) =>
        (await GetChainAsync(symbol, null, ct).ConfigureAwait(false)).Expirations;

    public async Task<OptionChain> GetChainAsync(string symbol, DateOnly? expiration = null, CancellationToken ct = default)
    {
        var sym = ToYahoo(symbol);
        var date = expiration is { } e ? $"&date={new DateTimeOffset(e.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds()}" : "";
        return await Cache.GetOrAddAsync($"yahoo:opt:{sym}:{expiration}", TimeSpan.FromSeconds(30), async c =>
        {
            using var doc = await GetWithCrumbAsync(cr => $"{Q2}/v7/finance/options/{Enc(sym)}?crumb={Enc(cr)}{date}", c).ConfigureAwait(false);
            var chain = ParseOptions(doc.RootElement, Norm(symbol));
            if (chain.Expirations.Count == 0) throw NoData($"no options for {symbol}");
            return chain;
        }, ct).ConfigureAwait(false);
    }

    internal static OptionChain ParseOptions(JsonElement root, string underlying)
    {
        var r = root.Path("optionChain", "result")?.At(0);
        var exps = new List<DateOnly>();
        var contracts = new List<OptionContract>();
        double? spot = null; DateOnly? exp = null; DateTimeOffset? snapshot = null;
        if (r is { } res)
        {
            exps.AddRange(res.Prop("expirationDates").Items().Select(x => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)x.GetDouble()).UtcDateTime)));
            spot = res.Path("quote", "regularMarketPrice").Num();
            snapshot = res.Prop("quote") is { } uq ? uq.UnixSeconds("regularMarketTime") : null;
            var block = res.Prop("options")?.At(0);
            if (block is { } b)
            {
                if (b.UnixSeconds("expirationDate") is { } ed) exp = DateOnly.FromDateTime(ed.UtcDateTime);
                foreach (var (arr, right) in new[] { ("calls", OptionRight.Call), ("puts", OptionRight.Put) })
                    foreach (var c in b.Prop(arr).Items())
                    {
                        var sym = c.Str("contractSymbol");
                        if (sym is null || c.Num("strike") is not { } k) continue;
                        var cexp = c.UnixSeconds("expiration") is { } ce ? DateOnly.FromDateTime(ce.UtcDateTime) : exp ?? default;
                        if (OccSymbol.TryParse(sym, out _, out var occExp, out _, out _)) cexp = occExp;
                        var iv = c.Num("impliedVolatility");
                        var bid = c.Num("bid"); var ask = c.Num("ask");
                        // Yahoo reports a placeholder IV (~1e-5) when there is no two-sided market (e.g. pre-market).
                        if (iv is < 0.001 || (bid is null or 0 && ask is null or 0 && iv is < 0.01)) iv = null;
                        contracts.Add(new OptionContract
                        {
                            ContractSymbol = sym, Underlying = underlying, Right = right, Strike = k, Expiration = cexp,
                            Bid = bid is > 0 ? bid : null, Ask = ask is > 0 ? ask : null,
                            Last = c.Num("lastPrice"), Change = c.Num("change"), ChangePercent = c.Num("percentChange"),
                            Volume = c.Num("volume"), OpenInterest = c.Num("openInterest"), ImpliedVolatility = iv,
                            LastTradeTime = c.UnixSeconds("lastTradeDate"), InTheMoney = c.Bool("inTheMoney"),
                        });
                    }
            }
        }
        return new OptionChain
        {
            Underlying = underlying, UnderlyingPrice = spot, Expirations = exps, Contracts = contracts, Expiration = exp,
            // F4: snapshot time = the underlying quote time Yahoo embeds in the chain response (not the download time).
            Source = "yahoo", DelayMinutes = 15, AsOf = snapshot,
        };
    }

    // ---------------- fundamentals ----------------

    public async Task<Fundamentals> GetFundamentalsAsync(string symbol, CancellationToken ct = default)
    {
        var sym = ToYahoo(symbol);
        return await Cache.GetOrAddAsync($"yahoo:fund:{sym}", TimeSpan.FromMinutes(30), async c =>
        {
            const string modules = "price,summaryDetail,defaultKeyStatistics,financialData,assetProfile,calendarEvents,quoteType";
            using var doc = await GetWithCrumbAsync(cr => $"{Q2}/v10/finance/quoteSummary/{Enc(sym)}?modules={modules}&crumb={Enc(cr)}", c).ConfigureAwait(false);
            return ParseQuoteSummary(doc.RootElement, Norm(symbol)) ?? throw NoData($"no fundamentals for {symbol}");
        }, ct).ConfigureAwait(false);
    }

    internal static Fundamentals? ParseQuoteSummary(JsonElement root, string symbol)
    {
        if (root.Path("quoteSummary", "result")?.At(0) is not { } r) return null;
        var price = r.Prop("price"); var sd = r.Prop("summaryDetail"); var ks = r.Prop("defaultKeyStatistics");
        var fd = r.Prop("financialData"); var ap = r.Prop("assetProfile"); var ce = r.Prop("calendarEvents");
        double? N(JsonElement? m, string k) => m is { } x ? x.Num(k) : null;
        string? S(JsonElement? m, string k) => m is { } x ? x.Str(k) : null;
        DateOnly? D(JsonElement? e) => e.Num() is { } s && s > 0 ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)s).UtcDateTime) : null;
        return new Fundamentals
        {
            Symbol = symbol,
            Name = S(price, "longName") ?? S(price, "shortName"),
            QuoteType = S(price, "quoteType") ?? S(r.Prop("quoteType"), "quoteType"),
            Exchange = S(price, "exchangeName"),
            Sector = S(ap, "sector"), Industry = S(ap, "industry"), Description = S(ap, "longBusinessSummary"),
            Website = S(ap, "website"), Country = S(ap, "country"),
            Employees = N(ap, "fullTimeEmployees") is { } emp ? (int)emp : null,
            MarketCap = N(price, "marketCap") ?? N(sd, "marketCap"),
            SharesOutstanding = N(ks, "sharesOutstanding"),
            TrailingPE = N(sd, "trailingPE"), ForwardPE = N(sd, "forwardPE") ?? N(ks, "forwardPE"),
            Eps = N(ks, "trailingEps"), Beta = N(sd, "beta") ?? N(ks, "beta"),
            DividendYield = N(sd, "dividendYield") ?? N(sd, "yield"), DividendRate = N(sd, "dividendRate"),
            FiftyTwoWeekHigh = N(sd, "fiftyTwoWeekHigh"), FiftyTwoWeekLow = N(sd, "fiftyTwoWeekLow"),
            AverageVolume = N(sd, "averageVolume"),
            ProfitMargin = N(fd, "profitMargins") ?? N(ks, "profitMargins"), Revenue = N(fd, "totalRevenue"),
            TargetPrice = N(fd, "targetMeanPrice"), PriceToBook = N(ks, "priceToBook"),
            NextEarningsDate = D(ce?.Path("earnings", "earningsDate")?.At(0)),
            ExDividendDate = D(sd?.Prop("exDividendDate")) ?? D(ce?.Prop("exDividendDate")),
            Source = "yahoo",
        };
    }

    // ---------------- financial statements ----------------

    private static readonly (string Type, FinancialStatementKind Kind, string Item)[] TimeseriesTypes =
    [
        ("annualTotalRevenue", FinancialStatementKind.Income, FinancialItems.TotalRevenue),
        ("annualGrossProfit", FinancialStatementKind.Income, FinancialItems.GrossProfit),
        ("annualOperatingIncome", FinancialStatementKind.Income, FinancialItems.OperatingIncome),
        ("annualNetIncome", FinancialStatementKind.Income, FinancialItems.NetIncome),
        ("annualDilutedEPS", FinancialStatementKind.Income, FinancialItems.DilutedEps),
        ("annualTotalAssets", FinancialStatementKind.Balance, FinancialItems.TotalAssets),
        ("annualTotalLiabilitiesNetMinorityInterest", FinancialStatementKind.Balance, FinancialItems.TotalLiabilities),
        ("annualStockholdersEquity", FinancialStatementKind.Balance, FinancialItems.StockholdersEquity),
        ("annualOperatingCashFlow", FinancialStatementKind.CashFlow, FinancialItems.OperatingCashFlow),
        ("annualCapitalExpenditure", FinancialStatementKind.CashFlow, FinancialItems.CapitalExpenditure),
        ("annualFreeCashFlow", FinancialStatementKind.CashFlow, FinancialItems.FreeCashFlow),
    ];

    public async Task<FinancialStatements> GetFinancialsAsync(string symbol, CancellationToken ct = default)
    {
        var sym = ToYahoo(symbol);
        return await Cache.GetOrAddAsync($"yahoo:fin:{sym}", TimeSpan.FromHours(6), async c =>
        {
            var types = string.Join(",", TimeseriesTypes.Select(t => t.Type));
            var url = $"{Q2}/ws/fundamentals-timeseries/v1/finance/timeseries/{Enc(sym)}?type={types}&period1=1420070400&period2={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            using var doc = await GetJsonAsync(url, c, timeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);
            var fs = ParseTimeseries(doc.RootElement, Norm(symbol));
            if (fs.Lines.Count == 0) throw NoData($"no financials for {symbol}");
            return fs;
        }, ct).ConfigureAwait(false);
    }

    internal static FinancialStatements ParseTimeseries(JsonElement root, string symbol)
    {
        var map = TimeseriesTypes.ToDictionary(t => t.Type);
        var lines = new List<FinancialLine>();
        foreach (var r in root.Path("timeseries", "result").Items())
        {
            var type = r.Path("meta", "type")?.At(0).Str();
            if (type is null || !map.TryGetValue(type, out var t)) continue;
            foreach (var v in r.Prop(type).Items())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                if (v.Str("asOfDate") is not { } ds || !DateOnly.TryParse(ds, System.Globalization.CultureInfo.InvariantCulture, out var d)) continue;
                if (v.Path("reportedValue", "raw").Num() is not { } val) continue;
                lines.Add(new FinancialLine(t.Kind, t.Item, d, val));
            }
        }
        return new FinancialStatements { Symbol = symbol, Lines = lines, Source = "yahoo" };
    }

    // ---------------- news & search ----------------

    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, int count = 20, CancellationToken ct = default)
    {
        var sym = ToYahoo(symbol);
        using var doc = await GetJsonAsync($"{Q2}/v1/finance/search?q={Enc(sym)}&quotesCount=0&newsCount={Math.Clamp(count, 1, 50)}", ct).ConfigureAwait(false);
        var items = ParseNews(doc.RootElement);
        if (items.Count == 0) throw NoData($"no news for {symbol}");
        return items;
    }

    internal static IReadOnlyList<NewsItem> ParseNews(JsonElement root) =>
        root.Prop("news").Items().Select(n => (n.Str("title"), n.Str("link"), n)).Where(x => x.Item1 is not null && x.Item2 is not null)
            .Select(x => new NewsItem
            {
                Title = x.Item1!, Url = x.Item2!, Publisher = x.n.Str("publisher"), Published = x.n.UnixSeconds("providerPublishTime"),
                ThumbnailUrl = x.n.Path("thumbnail", "resolutions")?.At(0)?.Str("url"),
                RelatedSymbols = x.n.Prop("relatedTickers").Items().Select(t => t.Str() ?? "").Where(t => t != "").ToArray(),
                Source = "yahoo",
            }).ToList();

    public async Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        using var doc = await GetJsonAsync($"{Q2}/v1/finance/search?q={Enc(query.Trim())}&quotesCount={Math.Clamp(limit, 1, 25)}&newsCount=0&enableFuzzyQuery=false", ct).ConfigureAwait(false);
        return ParseSearch(doc.RootElement);
    }

    internal static IReadOnlyList<SymbolMatch> ParseSearch(JsonElement root) =>
        root.Prop("quotes").Items()
            .Where(q => q.Str("symbol") is not null && (q.Str("quoteType") ?? "") is "EQUITY" or "ETF" or "INDEX" or "MUTUALFUND")
            .Select(q => new SymbolMatch(q.Str("symbol")!, q.Str("longname") ?? q.Str("shortname") ?? q.Str("symbol")!,
                q.Str("exchDisp") ?? q.Str("exchange"), q.Str("quoteType"), "yahoo"))
            .ToList();
}
