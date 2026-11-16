using System.Text.Json;
using System.Text.Json.Nodes;
using Meili.Analytics;
using Meili.Core.Models;
using Meili.Core.Pricing;
using Meili.Core.Settings;
using Meili.Core.Time;
using Meili.Data.Cache;
using Meili.Shared.Services;
using ARight = Meili.Analytics.OptionRight;

namespace Meili.Automation;

public sealed partial class AutomationService
{
    private object PreferencesSnapshot(AppSettings s) => new { dataDirectory = DirectoryInfo, s.Language, s.Theme, s.UpDownColors, s.UpDownColorsChosen, s.CloseAction, s.GuideDismissed, s.StrategyHintDismissed, s.Pricing, s.Alerts, s.Charts, s.Options, proxyMode = s.Network.ProxyMode, network = new { s.Network.ProxyMode, s.Network.RequestTimeoutSeconds, s.Network.MaxRetries }, externalChanges = "Desktop reload/restart required; proxy URLs and credentials are excluded." };
    private static KlineIndicatorOptions IndicatorOptions(IndicatorSettings p) => new() { SmaPeriod = p.MaPeriod, EmaPeriod = p.EmaPeriod, BollingerPeriod = p.BollPeriod, BollingerMultiplier = p.BollWidth, MacdFast = p.MacdFast, MacdSlow = p.MacdSlow, MacdSignal = p.MacdSignal, RsiPeriod = p.RsiPeriod, KdjPeriod = p.KdjPeriod, KdjM1 = p.KdjM1, KdjM2 = p.KdjM2 };
    private static void ValidateIndicators(IndicatorSettings p) { if (p.MacdFast >= p.MacdSlow) throw new ArgumentException("MACD fast must be below slow."); }
    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch) { if (value is JsonObject nested && target[key] is JsonObject child) Merge(child, nested); else target[key] = value?.DeepClone(); }
    }
    private async Task<object> ExecuteCoverageAsync(string operation, Input a, CancellationToken ct)
    {
        switch (operation)
        {
            case "clear_alert_history":
                { var store = Alerts(); store.ClearHistory(); return new { cleared = true, remaining = store.History.Count }; }
            case "move_watchlist_symbol":
                {
                    var store = new WatchlistStore(Settings()); var group = store.Groups.FirstOrDefault(g => g.Id == a.Text("groupId")) ?? throw new ArgumentException("Group not found.");
                    var index = group.Symbols.FindIndex(s => s == Symbol(a.Text("symbol"))); var target = a.Int("toIndex", 0, 0, 10000);
                    if (index < 0 || target >= group.Symbols.Count) throw new ArgumentException("Symbol or target index not found.");
                    if (index != target) store.Reorder(group.Id, index, target); return new { groups = store.Groups };
                }
            case "probe_data_sources":
                {
                    if (_sources is null && !_injectedMarket) await Market(ct);
                    var providers = diagnosticProviders ?? _sources?.Providers.Cast<Meili.Core.Abstractions.IDataProvider>().ToArray() ?? [];
                    var selected = providers.Where(p => !a.Has("sourceId") || p.Id == a.Text("sourceId")).ToArray();
                    if (a.Has("sourceId") && selected.Length == 0) throw new ArgumentException("Unknown registered source.");
                    var router = _sources?.Router ?? (_probeRouter ??= new Meili.Data.Routing.FailoverRouter(providers, new Meili.Data.Routing.HealthTracker(), Meili.Data.DataServices.DefaultOrder));
                    await Task.WhenAll(selected.Select(p => router.ProbeAsync(p, ct)));
                    return new { scope = "current_process_endpoint_probe", status = selected.Length == 0 ? "unavailable" : "completed", sources = selected.Select(p => { var h = router.Health.All.Single(h => h.Id == p.Id); return new { h.Id, state = h.State(DateTimeOffset.UtcNow), h.Successes, h.Failures, h.LastSuccessAt, h.LastLatency }; }).ToArray() };
                }
            case "update_preferences":
                {
                    if (!a.Value.EnumerateObject().Any()) throw new ArgumentException("Provide at least one preference.");
                    var store = Settings(); var node = JsonSerializer.SerializeToNode(store.Current, WireJson.Options)!.AsObject();
                    Merge(node, JsonNode.Parse(a.Value.GetRawText())!.AsObject());
                    var updated = node.Deserialize<AppSettings>(WireJson.Options)!; ValidateIndicators(updated.Charts.Indicators);
                    if (a.Has("upDownColors")) updated.UpDownColorsChosen = true;
                    store.Save(updated); return PreferencesSnapshot(store.Current);
                }
            case "list_strategy_presets": return Enum.GetValues<StrategyKind>().Where(k => k != StrategyKind.Custom).Select(k => new { kind = k, requiresFarExpiry = k is StrategyKind.CalendarCall or StrategyKind.CalendarPut or StrategyKind.DiagonalCall, defaultWingSteps = StrategyFromQuotes.DefaultWingSteps }).ToArray();
            case "build_strategy":
                {
                    var kind = Enum.Parse<StrategyKind>(a.Text("kind")); var expiry = a.Date("expiry")!.Value; var far = a.Date("farExpiry");
                    if (far <= expiry) throw new ArgumentException("farExpiry must be later than expiry.");
                    ListedQuote[] Read(string field) => a.Has(field) ? a.Value.GetProperty(field).EnumerateArray().Select(q => new ListedQuote(Enum.Parse<ARight>(q.GetProperty("right").GetString()!), q.GetProperty("strike").GetDouble(), q.GetProperty("premium").GetDouble(), q.TryGetProperty("iv", out var iv) ? iv.GetDouble() : double.NaN)).ToArray() : [];
                    var strategy = StrategyFromQuotes.TryBuild(kind, a.Positive("spot"), expiry, Read("quotes"), a.Int("quantity", 1, 1, 1000), a.Int("wingSteps", 2, 1, 100), far, Read("farQuotes"));
                    return new { status = strategy is null ? "insufficient_quotes" : "ok", kind, legs = strategy?.Legs.Select(ToInput).ToArray(), isExecutable = false };
                }
            case "strategy_what_if":
                {
                    var strategy = new OptionStrategy(Legs(a)); var at = a.Instant("valuationTime"); int days = a.Int("horizonDays", 0, 0, 3650);
                    if (at is null && (strategy.HasMixedExpiries || days > 0 || a.Has("volatilityShift"))) throw new ArgumentException("Mixed expiries, future horizons and IV shifts require valuationTime.");
                    var valuation = at is null ? null : new StrategyValuation(at.Value.AddDays(days), a.Number("rate", 0), a.Number("dividendYield", 0)) { DefaultVolatility = a.OptionalNumber("volatility"), VolatilityShift = a.Number("volatilityShift", 0) };
                    if (valuation is not null && strategy.Legs.Any(l => l.IsOption && valuation.YearsTo(l.Expiry!.Value) > 0 && !double.IsFinite(valuation.VolatilityFor(l)))) throw new ArgumentException("Live option legs require IV or volatility.");
                    var result = WhatIf.Capture(strategy, a.Number("spot"), valuation);
                    return new { valuation, assumptions = new { settlement = valuation is null ? "intrinsic_same_expiry" : "per_leg_expiry_BSM", model = "European Black-Scholes-Merton" }, result };
                }
            case "update_chart_drawing":
            case "delete_chart_drawing":
            case "get_chart_drawings":
            case "add_chart_drawing":
            case "clear_chart_drawings":
                {
                    var symbol = Symbol(a.Text("symbol")); var path = Path.Combine(DirectoryInfo.Path, "drawings.json");
                    if (File.Exists(path)) WireJson.Read<Dictionary<string, List<ChartDrawing>>>(JsonDocument.Parse(File.ReadAllText(path)).RootElement);
                    var store = new ChartDrawingStore(DirectoryInfo.Path);
                    if (operation == "clear_chart_drawings") store.Clear(symbol);
                    if (operation == "add_chart_drawing")
                    {
                        if (store.For(symbol).Count >= 200) throw new ArgumentException("At most 200 drawings per symbol.");
                        var kind = Enum.Parse<DrawingKind>(a.Text("kind")); var first = a.Instant("time1"); var last = a.Instant("time2");
                        if (kind == DrawingKind.TrendLine && (first is null || last is null || first == last || !a.Has("price2"))) throw new ArgumentException("Trend lines need time1, time2 and price2; timestamps must differ.");
                        store.Add(symbol, new(kind, a.Number("price1"), first, a.Number("price2", 0), last));
                    }
                    if (operation is "update_chart_drawing" or "delete_chart_drawing")
                        store.Edit(symbol, a.Int("index", 0, 0, 199), operation == "delete_chart_drawing" ? null : new ChartDrawing(Enum.Parse<DrawingKind>(a.Text("kind")), a.Number("price1"), a.Instant("time1"), a.Number("price2", 0), a.Instant("time2")), a.Text("revision"));
                    var revision = store.Revision(symbol);
                    var drawings = store.For(symbol);
                    var offset = a.Int("offset", 0, 0, int.MaxValue); var limit = a.Int("limit", operation == "get_chart_drawings" ? 100 : 200, 1, 500);
                    return new { symbol, revision, drawings = drawings.Skip(offset).Take(limit).ToArray(), total = drawings.Count, offset, nextOffset = (long)offset + limit < drawings.Count ? (int?)(offset + limit) : null };
                }
            case "get_chart_analysis":
                {
                    var interval = a.Text("interval", "1d") switch { "1m" => BarInterval.Minute1, "5m" => BarInterval.Minute5, "15m" => BarInterval.Minute15, "30m" => BarInterval.Minute30, "1h" => BarInterval.Hour1, "1d" => BarInterval.Day1, "1w" => BarInterval.Week1, "1mo" => BarInterval.Month1, _ => throw new ArgumentException("Invalid interval.") };
                    var from = a.Instant("from"); var to = a.Instant("to"); if (from > to) throw new ArgumentException("from must not be after to.");
                    var request = new BarRequest(Symbol(a.Text("symbol")), interval, from, to, a.Text("adjust", "split") == "total" ? AdjustMode.Total : AdjustMode.Split);
                    var data = await Market(ct); var bars = await data.GetBarsAsync(request, ct);
                    var options = a.Has("indicators") ? WireJson.Read<IndicatorSettings>(a.Value.GetProperty("indicators")) : Settings().Current.Charts.Indicators; ValidateIndicators(options);
                    var snapshot = KlineSnapshot.Compute(bars.Bars.Select(b => new OhlcvBar(b.Time, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray(), IndicatorOptions(options), bars.Bars.Select(b => b.Incomplete || !b.IsConsistent()).ToArray());
                    object? comparison = null;
                    if (a.Has("compareSymbol"))
                    {
                        var other = await data.GetBarsAsync(request with { Symbol = Symbol(a.Text("compareSymbol")) }, ct);
                        var map = other.Bars.GroupBy(b => b.Time).ToDictionary(g => g.Key, g => g.Last().Close);
                        var shared = bars.Bars.Where(b => double.IsFinite(b.Close) && map.TryGetValue(b.Time, out var close) && double.IsFinite(close) && close > 0).ToArray();
                        var scale = shared.Length == 0 ? double.NaN : shared[0].Close / map[shared[0].Time];
                        comparison = new { other.Symbol, other.Source, other.AsOf, other.FetchedAt, other.Adjustment, basisMatches = bars.Adjustment == PriceAdjustment.Unknown || other.Adjustment == PriceAdjustment.Unknown ? (bool?)null : other.Adjustment == bars.Adjustment, status = shared.Length == 0 ? "no_common_prices" : "ok", page = Page(shared.Select(b => new { b.Time, close = map[b.Time] * scale }).ToArray(), a) };
                    }
                    var rows = bars.Bars.Select((b, i) => new { b.Time, open = b.Incomplete || !b.IsConsistent() ? (double?)null : b.Open, high = b.Incomplete || !b.IsConsistent() ? (double?)null : b.High, low = b.Incomplete || !b.IsConsistent() ? (double?)null : b.Low, b.Close, volume = b.Volume >= 0 && double.IsFinite(b.Volume) ? (double?)b.Volume : null, incomplete = b.Incomplete || !b.IsConsistent(), sma = snapshot.Sma[i], ema = snapshot.Ema[i], bollinger = new { middle = snapshot.Bollinger.Middle[i], upper = snapshot.Bollinger.Upper[i], lower = snapshot.Bollinger.Lower[i] }, macd = new { value = snapshot.Macd.Macd[i], signal = snapshot.Macd.Signal[i], histogram = snapshot.Macd.Histogram[i] }, rsi = snapshot.Rsi[i], kdj = new { k = snapshot.Kdj.K[i], d = snapshot.Kdj.D[i], j = snapshot.Kdj.J[i] } }).ToArray();
                    return new { bars.Symbol, bars.Interval, bars.Source, bars.AsOf, bars.FetchedAt, bars.DelayMinutes, bars.IsRealtime, bars.OfflineCache, bars.Adjustment, options, page = Page(rows, a), comparison, warmup = "null means insufficient history or incomplete input; indicators computed before pagination" };
                }
            case "get_pricing_assumptions":
            case "analyze_option_chain":
            case "get_volatility_insights":
            case "record_option_snapshot":
                return await OptionCoverageAsync(operation, a, ct);
            default: throw new ArgumentException("Unsupported operation.");
        }
    }
    private static LegInput ToInput(StrategyLeg leg) => new(leg.Type, leg.Side, leg.Quantity, leg.Premium, leg.Strike, leg.Expiry, leg.HasImpliedVolatility ? leg.ImpliedVolatility : null, leg.Multiplier);
    private async Task<object> OptionCoverageAsync(string operation, Input a, CancellationToken ct)
    {
        var symbol = Symbol(a.Text("symbol")); var data = await Market(ct); var rates = new RiskFreeRateProvider(data, () => Settings().Current.Pricing);
        if (operation == "get_pricing_assumptions")
        {
            var pricingInputs = await new PricingAssumptions(data, rates).ForAsync(symbol, "automation", ct);
            return new { symbol, rate = pricingInputs.Rate, dividendYield = pricingInputs.DividendYield, dividendStatus = pricingInputs.DividendObservation?.Reported == true ? "reported" : "assumed_zero", source = pricingInputs.DividendObservation?.Source, asOf = pricingInputs.DividendObservation?.AsOf, fetchedAt = pricingInputs.DividendObservation?.FetchedAt };
        }
        var now = a.Instant("valuationTime") ?? DateTimeOffset.UtcNow;
        var db = new MarketDb(Path.Combine(DirectoryInfo.Path, "cache", "market.db"));
        if (operation is "get_volatility_insights" or "record_option_snapshot")
        {
            var iv = new IvSource(_sources is null ? data.GetQuotesAsync : _sources.Providers.OfType<Meili.Data.Providers.Cboe.CboeProvider>().Single().GetQuotesAsync, data, rates);
            var insights = new VolatilityInsights(iv, data, rates, db);
            var sample = await iv.GetAsync(symbol, now, ct);
            if (operation == "record_option_snapshot")
            {
                var chain = await data.GetChainAsync(symbol, a.Date("expiry"), ct); var day = DateOnly.FromDateTime(MarketClock.ToEastern(chain.AsOf ?? now));
                var previous = await insights.SwapOpenInterestAsync(symbol, day, chain.Contracts).WaitAsync(ct);
                if (sample is not null) db.AddIvSample(symbol, sample.AsOf ?? now, sample.Basis.ToString(), sample.Iv, sample.Source);
                return new { symbol, day, chain.Source, chain.AsOf, oiRecorded = chain.Contracts.Count(c => c.OpenInterest is >= 0), ivRecorded = sample is not null, ivSample = sample, previousContracts = previous.Count };
            }
            var daily = IvRank.LastPerDay(sample is null ? [] : db.LoadIvSamples(symbol, now.AddDays(-370)).Where(s => s.T <= now && s.Kind == sample.Basis.ToString()).Select(s => (DateOnly.FromDateTime(MarketClock.ToEastern(s.T)), s.Iv)));
            var today = DateOnly.FromDateTime(MarketClock.ToEastern(now));
            var rank = sample is null ? null : IvRank.Compute(daily, today, sample.Iv);
            var expirations = await data.GetExpirationsAsync(symbol, ct); var quote = (await data.GetQuotesAsync([symbol], ct)).FirstOrDefault(q => q.Symbol == symbol);
            var term = await insights.TermStructureAsync(symbol, expirations, quote?.Price ?? 0, now, ct);
            return new { symbol, sample, rank, rankStatus = sample is null ? "no_current_iv" : rank is null ? "insufficient_history" : "ok", historyDays = daily.Count(d => d.Day < today), requiredDays = IvRank.MinDays, term, historyScope = "local_recorded_samples_only; read does not record history" };
        }
        var s = await data.GetChainAsync(symbol, a.Date("expiry"), ct); var input = await new PricingAssumptions(data, rates).ForAsync(symbol, "automation", ct);
        var quotes = await SafeData.QuotesAsync(data, symbol, "automation", ct); var underlyingQuote = quotes.FirstOrDefault(q => q.Symbol == symbol);
        static double? Positive(double? n) => n is > 0 && double.IsFinite(n.Value) ? n : null;
        var chainSpot = Positive(s.UnderlyingPrice); var liveSpot = Positive(underlyingQuote?.Price); var previousSpot = Positive(underlyingQuote?.PreviousClose);
        var spot = chainSpot ?? liveSpot ?? previousSpot;
        var at = a.Instant("valuationTime") ?? DataFreshness.SnapshotTime(s, now); var settings = input.Enrichment(at); var dayOi = DateOnly.FromDateTime(MarketClock.ToEastern(s.AsOf ?? now)); var before = db.LoadPreviousOpenInterest(symbol, dayOi);
        var contracts = s.Contracts.Where(c => !a.Has("expiry") || c.Expiration == a.Date("expiry")).OrderBy(c => c.Expiration).ThenBy(c => c.Strike).ThenBy(c => c.Right).ToArray();
        var maxOi = contracts.GroupBy(c => (c.Expiration, c.Right)).ToDictionary(g => g.Key, g => g.Max(c => c.OpenInterest is >= 0 ? c.OpenInterest.Value : 0));
        var rows = contracts.Select(c => new
        {
            c.ContractSymbol,
            c.Right,
            c.Strike,
            c.Expiration,
            c.Bid,
            c.Ask,
            c.Last,
            c.Volume,
            c.OpenInterest,
            entryReference = OptionEntryReference.From(c),
            enrichment = OptionEnricher.Enrich(c.Right == Meili.Core.Models.OptionRight.Call ? ARight.Call : ARight.Put, spot ?? 0, c.Strike, c.Expiration, c.ImpliedVolatility, c.Bid, c.Ask, c.Last, c.Delta, c.Gamma, c.Theta, c.Vega, c.Rho, c.TheoreticalPrice, c.InTheMoney, settings),
            spread = OptionQuoteMetrics.SpreadOf(c.Bid, c.Ask).Spread,
            spreadPercent = OptionQuoteMetrics.SpreadOf(c.Bid, c.Ask).Pct,
            isWideSpread = OptionQuoteMetrics.SpreadOf(c.Bid, c.Ask).Wide,
            distance = spot is > 0 ? (c.Strike - spot) / spot : null,
            oiRatio = maxOi[(c.Expiration, c.Right)] > 0 && c.OpenInterest is >= 0 ? c.OpenInterest / maxOi[(c.Expiration, c.Right)] : null,
            oiChange = c.OpenInterest is >= 0 && before.TryGetValue(c.ContractSymbol, out var prior) ? c.OpenInterest - prior : (double?)null
        }).ToArray();
        return new { s.Underlying, spot, spotBasis = chainSpot is not null ? "chain" : liveSpot is not null ? "quote" : previousSpot is not null ? "previous_close" : "missing", underlyingQuote, spotStatus = spot is null ? "missing" : "available", s.Source, s.AsOf, s.FetchedAt, s.DelayMinutes, s.IsRealtime, valuationTime = at, assumptions = new { input.Rate, input.DividendYield, dividendStatus = input.DividendObservation?.Reported == true ? "reported" : "assumed_zero", model = "European Black-Scholes-Merton" }, page = Page(rows, a), oiScope = "previous_local_recorded_day; missing stays null", smile = "per-contract enrichment.impliedVolatility grouped by expiration/right/strike" };
    }
}
