namespace Meili.Core.Models;

/// <summary>What a data provider can do. Used for routing and for the data-source status page.</summary>
[Flags]
public enum DataCapability
{
    None = 0,
    Quote = 1,
    IntradayBars = 2,
    DailyBars = 4,
    Options = 8,
    Fundamentals = 16,
    Financials = 32,
    News = 64,
    Search = 128,
}

public enum MarketState { Unknown, PreMarket, Regular, AfterHours, Closed }

public enum BarInterval { Minute1, Minute5, Minute15, Minute30, Hour1, Day1, Week1, Month1 }

public static class BarIntervalExtensions
{
    public static bool IsIntraday(this BarInterval i) => i < BarInterval.Day1;
    public static TimeSpan Duration(this BarInterval i) => i switch
    {
        BarInterval.Minute1 => TimeSpan.FromMinutes(1),
        BarInterval.Minute5 => TimeSpan.FromMinutes(5),
        BarInterval.Minute15 => TimeSpan.FromMinutes(15),
        BarInterval.Minute30 => TimeSpan.FromMinutes(30),
        BarInterval.Hour1 => TimeSpan.FromHours(1),
        BarInterval.Day1 => TimeSpan.FromDays(1),
        BarInterval.Week1 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(30),
    };
}

/// <summary>Common provenance metadata carried by every piece of market data.</summary>
public abstract record SourcedData
{
    /// <summary>Provider id, e.g. "yahoo", "cboe".</summary>
    public string Source { get; init; } = "";
    /// <summary>
    /// Market time the values refer to (last trade / source snapshot time), only when the source states it.
    /// Never the download time: that is <see cref="FetchedAt"/>.
    /// </summary>
    public DateTimeOffset? AsOf { get; init; }
    /// <summary>When this app received the data (stamped by the router).</summary>
    public DateTimeOffset? FetchedAt { get; init; }
    /// <summary>True only when the source itself says the data is real-time.</summary>
    public bool IsRealtime { get; init; }
    /// <summary>Delay the source declares, in minutes (0 = real-time). <c>null</c> = the source does not say.</summary>
    public int? DelayMinutes { get; init; }
}

public sealed record Quote : SourcedData
{
    public required string Symbol { get; init; }
    public string? Name { get; init; }
    public string? Exchange { get; init; }
    public string Currency { get; init; } = "USD";
    public double Price { get; init; }
    public double? Change { get; init; }
    public double? ChangePercent { get; init; }
    public double? PreviousClose { get; init; }
    public double? Open { get; init; }
    public double? High { get; init; }
    public double? Low { get; init; }
    public double? Volume { get; init; }
    public double? Bid { get; init; }
    public double? Ask { get; init; }
    public double? BidSize { get; init; }
    public double? AskSize { get; init; }
    public double? FiftyTwoWeekHigh { get; init; }
    public double? FiftyTwoWeekLow { get; init; }
    public double? MarketCap { get; init; }
    /// <summary>B1: the source's 30-day implied volatility index as a fraction (CBOE "iv30"), when it publishes one.</summary>
    public double? ImpliedVolatility30 { get; init; }
    public MarketState MarketState { get; init; }
    /// <summary>Pre-market or after-hours last price, when the market is in an extended session.</summary>
    public double? ExtendedPrice { get; init; }
    public double? ExtendedChange { get; init; }
    public double? ExtendedChangePercent { get; init; }
    public DateTimeOffset? ExtendedTime { get; init; }
}

/// <summary>
/// One OHLCV bar. Close is always a real traded price; Open/High/Low are <see cref="double.NaN"/> when the source did not
/// supply them (F2: never fabricated from Close). <see cref="Incomplete"/> marks bars whose O/H/L are missing or
/// contradict each other (e.g. Low above Open) — charts draw those as a close-only point, KDJ skips them.
/// </summary>
public sealed record Bar(DateTimeOffset Time, double Open, double High, double Low, double Close, double Volume)
{
    public bool Incomplete { get; init; }

    /// <summary>Builds a bar from raw source values without inventing anything: missing O/H/L stay NaN and the bar is flagged.</summary>
    public static Bar FromSource(DateTimeOffset time, double? open, double? high, double? low, double close, double? volume)
    {
        static double Px(double? v) => v is double d && double.IsFinite(d) && d > 0 ? d : double.NaN;
        var bar = new Bar(time, Px(open), Px(high), Px(low), close, volume is double vv && double.IsFinite(vv) && vv >= 0 ? vv : 0);
        return bar with { Incomplete = !bar.IsConsistent() };
    }

    /// <summary>A close-only point (e.g. an intraday price tick): O/H/L unknown.</summary>
    public static Bar CloseOnly(DateTimeOffset time, double close, double volume) =>
        new(time, double.NaN, double.NaN, double.NaN, close, volume) { Incomplete = true };

    /// <summary>True when O/H/L/C are all present and Low ≤ min(Open, Close) ≤ max(Open, Close) ≤ High.</summary>
    public bool IsConsistent()
    {
        if (!double.IsFinite(Open) || !double.IsFinite(High) || !double.IsFinite(Low) || !double.IsFinite(Close)) return false;
        // Tolerate float noise from sources that round differently per field.
        var eps = Math.Abs(Close) * 1e-9;
        return Low <= Math.Min(Open, Close) + eps && High >= Math.Max(Open, Close) - eps && Low <= High + eps;
    }
}

public sealed record BarSeries : SourcedData
{
    public required string Symbol { get; init; }
    public required BarInterval Interval { get; init; }
    public required IReadOnlyList<Bar> Bars { get; init; }
    /// <summary>
    /// B2: true when the network fetch failed and these bars come only from the local cache (offline view).
    /// <see cref="SourcedData.FetchedAt"/> then is when the cached data was downloaded.
    /// </summary>
    public bool OfflineCache { get; init; }
    /// <summary>F7: how the source adjusted historical prices.</summary>
    public PriceAdjustment Adjustment { get; init; }
}

/// <summary>F7: price-adjustment basis of a bar series.</summary>
public enum PriceAdjustment
{
    /// <summary>The source does not say.</summary>
    Unknown = 0,
    /// <summary>Raw traded prices.</summary>
    None,
    /// <summary>Adjusted for splits only; dividends not deducted (Yahoo/Nasdaq default).</summary>
    Split,
    /// <summary>Adjusted for splits and dividends (Yahoo adjclose, 前复权 from Tencent/Eastmoney).</summary>
    SplitDividend,
}

/// <summary>F7: what the user asked for. <see cref="Split"/> is the default and matches most free sources.</summary>
public enum AdjustMode
{
    Split,
    /// <summary>Total-return view: splits and dividends (Yahoo adjclose).</summary>
    Total,
}

public static class AdjustModeExtensions
{
    /// <summary>B2 cache key (<c>adjust</c> column). Split keeps "" so caches written before F7 stay valid.</summary>
    public static string CacheKey(this AdjustMode m) => m == AdjustMode.Total ? "total" : "";
}

public sealed record BarRequest(string Symbol, BarInterval Interval, DateTimeOffset? From = null, DateTimeOffset? To = null,
    AdjustMode Adjust = AdjustMode.Split);

public enum OptionRight { Call, Put }

public sealed record OptionContract
{
    public required string ContractSymbol { get; init; }
    public required string Underlying { get; init; }
    public required OptionRight Right { get; init; }
    public required double Strike { get; init; }
    public required DateOnly Expiration { get; init; }
    public double? Bid { get; init; }
    public double? Ask { get; init; }
    public double? BidSize { get; init; }
    public double? AskSize { get; init; }
    public double? Last { get; init; }
    public double? Change { get; init; }
    public double? ChangePercent { get; init; }
    public double? Volume { get; init; }
    public double? OpenInterest { get; init; }
    /// <summary>Implied volatility as a fraction (0.25 = 25%).</summary>
    public double? ImpliedVolatility { get; init; }
    public double? Delta { get; init; }
    public double? Gamma { get; init; }
    public double? Theta { get; init; }
    public double? Vega { get; init; }
    public double? Rho { get; init; }
    public double? TheoreticalPrice { get; init; }
    public DateTimeOffset? LastTradeTime { get; init; }
    public bool? InTheMoney { get; init; }
    public double? Mid => Bid is > 0 && Ask is > 0 ? (Bid + Ask) / 2 : null;
}

public sealed record OptionChain : SourcedData
{
    public required string Underlying { get; init; }
    public double? UnderlyingPrice { get; init; }
    /// <summary>All expirations the source knows about.</summary>
    public required IReadOnlyList<DateOnly> Expirations { get; init; }
    /// <summary>Contracts for <see cref="Expiration"/> (or for all expirations when the source returns everything at once and Expiration is null).</summary>
    public required IReadOnlyList<OptionContract> Contracts { get; init; }
    public DateOnly? Expiration { get; init; }
}

public enum EventDateStatus { Unknown, Estimated, SourceConfirmed }

public sealed record Fundamentals : SourcedData
{
    public required string Symbol { get; init; }
    public string? Name { get; init; }
    public string? QuoteType { get; init; }
    public string? Exchange { get; init; }
    public string? Sector { get; init; }
    public string? Industry { get; init; }
    public string? Description { get; init; }
    public string? Website { get; init; }
    public string? Country { get; init; }
    public int? Employees { get; init; }
    public double? MarketCap { get; init; }
    public double? SharesOutstanding { get; init; }
    public double? TrailingPE { get; init; }
    public double? ForwardPE { get; init; }
    public double? Eps { get; init; }
    public double? Beta { get; init; }
    public double? DividendYield { get; init; }
    public double? DividendRate { get; init; }
    public double? FiftyTwoWeekHigh { get; init; }
    public double? FiftyTwoWeekLow { get; init; }
    public double? AverageVolume { get; init; }
    public double? ProfitMargin { get; init; }
    public double? Revenue { get; init; }
    public double? TargetPrice { get; init; }
    public double? PriceToBook { get; init; }
    public DateOnly? NextEarningsDate { get; init; }
    public DateOnly? EarningsDateEnd { get; init; }
    public EventDateStatus EarningsDateStatus { get; init; }
    public EventDateStatus ExDividendDateStatus { get; init; }
    public DateOnly? ExDividendDate { get; init; }
}

public enum FinancialStatementKind { Income, Balance, CashFlow }

/// <summary>Canonical line-item keys shared by all providers.</summary>
public static class FinancialItems
{
    public const string TotalRevenue = "TotalRevenue";
    public const string GrossProfit = "GrossProfit";
    public const string OperatingIncome = "OperatingIncome";
    public const string NetIncome = "NetIncome";
    public const string DilutedEps = "DilutedEPS";
    public const string TotalAssets = "TotalAssets";
    public const string TotalLiabilities = "TotalLiabilities";
    public const string StockholdersEquity = "StockholdersEquity";
    public const string OperatingCashFlow = "OperatingCashFlow";
    public const string CapitalExpenditure = "CapitalExpenditure";
    public const string FreeCashFlow = "FreeCashFlow";
}

public sealed record FinancialLine(FinancialStatementKind Statement, string Item, DateOnly PeriodEnd, double Value, bool Annual = true);

public sealed record FinancialStatements : SourcedData
{
    public required string Symbol { get; init; }
    public required IReadOnlyList<FinancialLine> Lines { get; init; }
}

public sealed record NewsItem
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Publisher { get; init; }
    public DateTimeOffset? Published { get; init; }
    public string? Summary { get; init; }
    public string? ThumbnailUrl { get; init; }
    public IReadOnlyList<string> RelatedSymbols { get; init; } = [];
    public string Source { get; init; } = "";
}

public sealed record SymbolMatch(string Symbol, string Name, string? Exchange, string? Type, string Source);
