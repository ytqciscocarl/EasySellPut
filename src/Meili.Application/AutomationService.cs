using System.Globalization;
using System.Text.Json;
using Meili.Analytics;
using Meili.Core.Abstractions;
using Meili.Core.Alerts;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Data;
using Meili.Data.Cache;
using Meili.Data.Http;
using Meili.Shared.Services;
using Right = Meili.Analytics.OptionRight;

namespace Meili.Automation;

/// <summary>UI-free application boundary shared by CLI and MCP. Serializes requests within one host.</summary>
public sealed partial class AutomationService(DataDirectoryInfo directory, IMarketDataService? market = null, IReadOnlyList<IDataProvider>? diagnosticProviders = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly bool _injectedMarket = market is not null;
    private DataServices? _sources;
    private Meili.Data.Routing.FailoverRouter? _probeRouter;
    private IMarketDataService? _market = market;
    public DataDirectoryInfo DirectoryInfo { get; } = directory;

    public async Task<JsonElement> ExecuteAsync(string operation, JsonElement args, bool allowWrites, CancellationToken ct = default)
    {
        var op = OperationCatalog.Find(operation);
        OperationCatalog.Validate(op, args);
        if (op.Writes && !allowWrites) throw new UnauthorizedAccessException();
        await _gate.WaitAsync(ct);
        try { ct.ThrowIfCancellationRequested(); return WireJson.Element(WireJson.Success(await ExecuteCoreAsync(operation, new Input(args), ct))); }
        finally { _gate.Release(); }
    }

    private SettingsStore Settings()
    {
        CheckJson("settings.json");
        var store = new SettingsStore(DirectoryInfo.Path); store.Load(); return store;
    }
    private AlertStore Alerts()
    {
        CheckJson("alerts.json");
        var store = new AlertStore(DirectoryInfo.Path); store.Load(); return store;
    }
    private StrategyStore Strategies() { CheckJson("strategies.json"); return new(DirectoryInfo.Path); }
    private void CheckJson(string name)
    {
        var path = Path.Combine(DirectoryInfo.Path, name);
        if (!File.Exists(path)) return;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var text = File.ReadAllText(path);
        object? parsed = name switch { "settings.json" => JsonSerializer.Deserialize<AppSettings>(text, options), "alerts.json" => JsonSerializer.Deserialize<AlertDocument>(text, options), _ => JsonSerializer.Deserialize<List<SavedStrategy>>(text, options) };
        if (parsed is null) throw new JsonException($"{name} must not be null.");
    }
    private async Task<IMarketDataService> Market(CancellationToken ct)
    {
        if (_market is not null) return _market;
        var settings = Settings().Current.Network;
        var proxy = await ProxyDetector.ResolveAsync(settings, ct);
        _sources = new DataServices(settings, proxy);
        return _market = new CachedMarketData(_sources.Router, new MarketDb(Path.Combine(DirectoryInfo.Path, "cache", "market.db")));
    }
    private async Task<object?> ExecuteCoreAsync(string op, Input a, CancellationToken ct)
    {
        switch (op)
        {
            case "search_symbols": return await (await Market(ct)).SearchAsync(a.Text("query", max: 100), a.Int("limit", 10, 1, 50), ct);
            case "get_quotes":
                {
                    var symbols = a.Value.GetProperty("symbols").EnumerateArray().Select(x => Symbol(x.GetString()!)).Distinct().ToArray();
                    if (symbols.Length is < 1 or > 100) throw new ArgumentException("Provide 1..100 symbols.");
                    var quotes = await (await Market(ct)).GetQuotesAsync(symbols, ct);
                    return new { quotes, missingSymbols = symbols.Where(s => !quotes.Any(q => q.Symbol.Equals(s, StringComparison.OrdinalIgnoreCase))).ToArray() };
                }
            case "get_bars":
                {
                    var interval = a.Text("interval", "1d") switch { "1m" => BarInterval.Minute1, "5m" => BarInterval.Minute5, "15m" => BarInterval.Minute15, "30m" => BarInterval.Minute30, "1h" => BarInterval.Hour1, "1d" => BarInterval.Day1, "1w" => BarInterval.Week1, "1mo" => BarInterval.Month1, _ => throw new ArgumentException("Invalid interval.") };
                    var from = a.Instant("from"); var to = a.Instant("to"); if (from > to) throw new ArgumentException("from must not be after to.");
                    var s = await (await Market(ct)).GetBarsAsync(new(Symbol(a.Text("symbol")), interval, from, to, a.Text("adjust", "split") == "total" ? AdjustMode.Total : AdjustMode.Split), ct);
                    return new { s.Symbol, s.Interval, s.Source, s.AsOf, s.FetchedAt, s.IsRealtime, s.DelayMinutes, s.OfflineCache, s.Adjustment, page = Page(s.Bars, a) };
                }
            case "get_expirations": return await (await Market(ct)).GetExpirationsAsync(Symbol(a.Text("symbol")), ct);
            case "get_option_chain":
                {
                    var min = a.OptionalNumber("minStrike"); var max = a.OptionalNumber("maxStrike");
                    if (min < 0 || max < 0 || min > max) throw new ArgumentException("Invalid strike range.");
                    var s = await (await Market(ct)).GetChainAsync(Symbol(a.Text("symbol")), a.Date("expiry"), ct);
                    var requestedExpiry = a.Date("expiry");
                    var filtered = s.Contracts.Where(c => (!requestedExpiry.HasValue || c.Expiration == requestedExpiry.Value) && (!a.Has("right") || c.Right.ToString().Equals(a.Text("right"), StringComparison.OrdinalIgnoreCase)) && (!min.HasValue || c.Strike >= min) && (!max.HasValue || c.Strike <= max)).OrderBy(c => c.Expiration).ThenBy(c => c.Strike).ThenBy(c => c.Right).ToArray();
                    return new { s.Underlying, s.UnderlyingPrice, s.Expiration, s.Expirations, s.Source, s.AsOf, s.FetchedAt, s.IsRealtime, s.DelayMinutes, page = Page(filtered, a), entryReferences = Page(filtered.Select(c => new { c.ContractSymbol, reference = OptionEntryReference.From(c) }).ToArray(), a) };
                }
            case "get_fundamentals": return await (await Market(ct)).GetFundamentalsAsync(Symbol(a.Text("symbol")), ct);
            case "get_financials": return await (await Market(ct)).GetFinancialsAsync(Symbol(a.Text("symbol")), ct);
            case "get_news": return await (await Market(ct)).GetNewsAsync(Symbol(a.Text("symbol")), a.Int("limit", 20, 1, 50), ct);
            case "get_market_events": return await MarketTimelineService.LoadAsync(await Market(ct), Symbol(a.Text("symbol")), a.Text("type", "all"), a.Date("from"), a.Date("to"), a.Int("limit", 50, 1, 100), ct);
            case "get_source_health":
                {
                    if (_sources is null && !_injectedMarket) await Market(ct);
                    return new { scope = "current_process", sources = (_sources?.Health ?? _probeRouter?.Health)?.All.Select(h => new { h.Id, h.DisplayName, h.Capabilities, state = h.State(DateTimeOffset.UtcNow), h.Successes, h.Failures, h.LastSuccessAt, h.LastLatency }), note = "Unknown is not a connectivity check. Counters are not shared with the desktop." };
                }
            case "calculate_option":
                {
                    var right = a.Text("right") == "call" ? Right.Call : Right.Put;
                    double spot = a.Positive("spot"), strike = a.Positive("strike"), years = a.Number("years"), rate = a.Number("rate", 0), q = a.Number("dividendYield", 0), vol = a.Number("volatility");
                    if (years < 0 || vol < 0) throw new ArgumentException("years and volatility must be nonnegative.");
                    var iv = a.OptionalNumber("price") is { } price ? ImpliedVolatility.Solve(right, price, spot, strike, years, rate, q) : null;
                    var greeks = BlackScholes.Greeks(right, spot, strike, years, rate, q, vol);
                    return new { status = double.IsFinite(greeks.Price) ? "ok" : "unavailable", model = "European Black-Scholes-Merton", assumptions = new { right, spot, strike, years, rate, dividendYield = q, volatility = vol }, greeks, impliedVolatility = iv, ivStatus = !a.Has("price") ? "not_requested" : iv.HasValue ? "solved" : "unsolvable" };
                }
            case "calculate_indicators":
                {
                    var closes = a.Value.GetProperty("closes").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    if (closes.Length == 0) throw new ArgumentException("closes must not be empty.");
                    var p = a.Int("period", 14, 1, 1000);
                    return new { period = p, closes, sma = Indicators.Sma(closes, p), ema = Indicators.Ema(closes, p), rsi = Indicators.Rsi(closes, p), warmup = "null values indicate insufficient history" };
                }
            case "analyze_strategy": return Analyze(a);
            case "get_watchlists": return new WatchlistStore(Settings()).Groups;
            case "create_watchlist_group":
            case "rename_watchlist_group":
            case "delete_watchlist_group":
            case "add_watchlist_symbol":
            case "remove_watchlist_symbol":
                {
                    var store = new WatchlistStore(Settings()); WatchlistError error; WatchlistGroup? group = null;
                    switch (op)
                    {
                        case "create_watchlist_group": error = store.AddGroup(a.Text("name"), out group); break;
                        case "rename_watchlist_group": error = store.RenameGroup(a.Text("groupId"), a.Text("name")); break;
                        case "delete_watchlist_group": error = store.RemoveGroup(a.Text("groupId")); break;
                        case "add_watchlist_symbol": error = store.AddSymbol(a.Text("groupId"), Symbol(a.Text("symbol")), out _); break;
                        default: if (!store.RemoveSymbol(a.Text("groupId"), Symbol(a.Text("symbol")))) throw new ArgumentException("Symbol or group not found."); error = WatchlistError.None; break;
                    }
                    if (error != WatchlistError.None) throw new ArgumentException(error.ToString());
                    return new { group, groups = store.Groups };
                }
            case "get_saved_strategies": return Strategies().All;
            case "save_strategy":
                {
                    var legs = Legs(a); var name = a.Text("name"); var store = Strategies();
                    var saved = new SavedStrategy(name, Symbol(a.Text("symbol")), Enum.Parse<StrategyKind>(a.Text("kind", "Custom")), legs.Select(SavedLeg.From).ToArray(), DateTimeOffset.UtcNow);
                    store.Save(saved); return saved;
                }
            case "delete_strategy":
                {
                    var store = Strategies(); var name = a.Text("name");
                    if (!store.All.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Strategy not found.");
                    store.Delete(name); return new { deleted = name };
                }
            case "get_alerts": return Alerts().Alerts;
            case "get_alert_history": return Page(Alerts().History, a);
            case "save_alert":
                {
                    var store = Alerts(); var rule = a.Has("id") ? store.Find(a.Text("id")) ?? throw new ArgumentException("Alert not found.") : new PriceAlert();
                    rule.Symbol = Symbol(a.Text("symbol")); rule.Kind = Enum.Parse<AlertKind>(a.Text("kind")); rule.Threshold = a.Number("threshold");
                    if (rule.Threshold < 0) throw new ArgumentException("threshold must be nonnegative.");
                    rule.Enabled = a.Bool("enabled", rule.Enabled); rule.Repeat = a.Has("repeat") ? Enum.Parse<AlertRepeatMode>(a.Text("repeat")) : rule.Repeat;
                    rule.CooldownMinutes = a.Int("cooldownMinutes", rule.CooldownMinutes, 1, 1440); rule.IncludeExtendedHours = a.Bool("includeExtendedHours", rule.IncludeExtendedHours);
                    rule.Note = a.Text("note", rule.Note); rule.HasSeenQuote = false;
                    return store.Upsert(rule);
                }
            case "delete_alert":
                {
                    var id = a.Text("id"); if (!Alerts().Remove(id)) throw new ArgumentException("Alert not found."); return new { deleted = id };
                }
            case "set_alert_enabled":
                {
                    var store = Alerts(); var id = a.Text("id"); if (store.Find(id) is null) throw new ArgumentException("Alert not found.");
                    store.SetEnabled(id, a.Bool("enabled")); return store.Find(id);
                }
            case "get_settings":
                {
                    var s = Settings().Current; return PreferencesSnapshot(s);
                }
            default: return await ExecuteCoverageAsync(op, a, ct);
        }
    }

    private static object Analyze(Input a)
    {
        var strategy = new OptionStrategy(Legs(a)); double spot = a.Positive("spot");
        var time = a.Instant("valuationTime"); double rate = a.Number("rate", 0), q = a.Number("dividendYield", 0); var vol = a.OptionalNumber("volatility");
        if (vol < 0) throw new ArgumentException("volatility must be nonnegative.");
        var valuation = time.HasValue ? new StrategyValuation(time.Value, rate, q) { DefaultVolatility = vol } : null;
        if (strategy.HasMixedExpiries && valuation is null) throw new ArgumentException("Mixed expiries require valuationTime and leg IV or fallback volatility.");
        if (valuation is not null && strategy.Legs.Any(l => l.IsOption && valuation.YearsTo(l.Expiry!.Value) > 0 && !double.IsFinite(valuation.VolatilityFor(l)))) throw new ArgumentException("Live option legs require IV or fallback volatility.");
        var range = strategy.SuggestPriceRange(spot); double low = a.Number("low", range.Low), high = a.Number("high", range.High);
        if (low < 0 || high <= low) throw new ArgumentException("Require 0 <= low < high.");
        int count = a.Int("points", 101, 2, 1001);
        var prices = Enumerable.Range(0, count).Select(i => low + (high - low) * i / (count - 1)).ToArray();
        var horizon = strategy.HasMixedExpiries ? StrategyValuation.AtDate(strategy.EarliestExpiry!.Value, rate, q) with { DefaultVolatility = vol } : null;
        var payoff = horizon is null ? strategy.AnalyzeAtExpiry() : strategy.AnalyzeAt(horizon, low, high);
        return new
        {
            strategy.NetCost,
            strategy.HasMixedExpiries,
            strategy.EarliestExpiry,
            strategy.LatestExpiry,
            assumptions = new { spot, valuationTime = time, rate, dividendYield = q, volatility = vol },
            payoff,
            horizon = horizon is null ? "all_legs_expiry" : "earliest_expiry",
            greeks = valuation is null ? (StrategyGreeks?)null : strategy.GreeksAt(spot, valuation),
            probabilityOfProfit = valuation is not null && vol is > 0 ? StrategyRisk.ProbabilityOfProfit(strategy, spot, vol.Value, valuation) : (double?)null,
            probabilityStatus = valuation is not null && vol is > 0 ? "risk_neutral_model" : "requires_valuation_time_and_positive_fallback_volatility",
            margin = StrategyRisk.RegTMargin(strategy, spot),
            curve = new { prices, pnl = prices.Select(p => horizon is null ? strategy.PnlAtExpiry(p) : strategy.PnlAt(p, horizon)).ToArray(), valuationPnl = valuation is null ? null : prices.Select(p => strategy.PnlAt(p, valuation)).ToArray() },
            note = "Margin is a Reg-T estimate, not a broker requirement. Mixed-expiry payoff is valued at earliest expiry."
        };
    }
    private static StrategyLeg[] Legs(Input a) => a.Value.GetProperty("legs").EnumerateArray().Select(v => WireJson.Read<LegInput>(v).ToLeg()).ToArray();
    private static string Symbol(string text) => WatchlistStore.TryNormalizeSymbol(text, out var s, out _) ? s : throw new ArgumentException("Invalid ticker symbol.");
    private static object Page<T>(IReadOnlyList<T> items, Input a)
    {
        int offset = a.Int("offset", 0, 0, int.MaxValue), limit = a.Int("limit", 100, 1, 500);
        return new { total = items.Count, offset, limit, items = items.Skip(offset).Take(limit).ToArray(), nextOffset = offset < items.Count && items.Count - offset > limit ? (int?)(offset + limit) : null };
    }
    public void Dispose() { _sources?.Dispose(); _gate.Dispose(); }
}

public sealed record LegInput(LegType Type, PositionSide Side, int Quantity, double Premium, double Strike = 0, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateOnly? Expiry = null, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? Iv = null, int Multiplier = 100)
{
    public StrategyLeg ToLeg()
    {
        if (Quantity is < 1 or > 100000 || Multiplier is < 1 or > 100000) throw new ArgumentException("quantity and multiplier must be 1..100000.");
        return new(Type, Side, Quantity, Strike, Premium, Expiry, Iv ?? double.NaN, Multiplier);
    }
}

internal sealed class Input(JsonElement value)
{
    public JsonElement Value { get; } = value;
    public bool Has(string name) => Value.TryGetProperty(name, out _);
    public string Text(string name, string? fallback = null, int max = 200)
    {
        var s = Has(name) ? Value.GetProperty(name).GetString() : fallback;
        if (s is null || (fallback is null && string.IsNullOrWhiteSpace(s)) || s.Length > max) throw new ArgumentException($"Invalid {name}.");
        return s;
    }
    public double Number(string name, double? fallback = null) => Has(name) ? Value.GetProperty(name).GetDouble() : fallback ?? throw new ArgumentException($"Missing {name}.");
    public double? OptionalNumber(string name) => Has(name) ? Number(name) : null;
    public double Positive(string name) => Number(name) is var v && v > 0 ? v : throw new ArgumentException($"{name} must be positive.");
    public int Int(string name, int fallback, int min, int max)
    { var n = Has(name) ? Value.GetProperty(name).GetInt32() : fallback; return n >= min && n <= max ? n : throw new ArgumentException($"{name} must be between {min} and {max}."); }
    public bool Bool(string name, bool fallback = false) => Has(name) ? Value.GetProperty(name).GetBoolean() : fallback;
    public DateOnly? Date(string name) => Has(name) ? DateOnly.ParseExact(Text(name), "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    public DateTimeOffset? Instant(string name)
    {
        if (!Has(name)) return null;
        var s = Text(name); if (!s.Contains('T') || (!s.EndsWith('Z') && !System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d{2}:\d{2}$"))) throw new ArgumentException($"{name} needs an ISO-8601 instant with timezone.");
        return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
    }
}
