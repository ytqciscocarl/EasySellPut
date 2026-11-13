using Meili.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meili.Analytics;
using Meili.Shared.Services;
using Meili.Core.Abstractions;
using Meili.Core.Export;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Core.Time;
using Meili.Data.Routing;
using ARight = Meili.Analytics.OptionRight;
using CRight = Meili.Core.Models.OptionRight;

namespace Meili.Shared.ViewModels;

public enum StrikeWindowKind { Near5 = 5, Near10 = 10, Near20 = 20, All = 0 }

/// <summary>Options page: expiration picker, T-quote chain, local IV/Greeks fill-in.</summary>
public partial class OptionsViewModel : ObservableObject
{
    private readonly WatchlistStore _watchlist;
    private readonly IMarketDataService _data;
    private readonly LocalizationService _loc;
    private readonly SymbolContext _context;
    private readonly PricingAssumptions _pricing;
    private PricingInputs _inputs;
    private readonly IUiTimer _timer = Ui.Timer(RefreshCadence.OptionChain); // C3
    private CancellationTokenSource? _cts;
    private bool _active;
    private string? _lastError;
    private IReadOnlyList<OptionStrikeRow> _allRows = [];
    private bool _suppressExpChange;
    private string? _loadedSymbol;
    private OptionChain? _lastChain;
    // E4: inputs of the header texts, so a language switch can relabel them without a reload.
    private string? _hdrSymbol;
    private Quote? _hdrQuote;
    private bool _hdrLocal;

    public ObservableCollection<string> WatchSymbols { get; } = [];
    public ObservableCollection<ExpirationChoice> Expirations { get; } = [];
    public ObservableCollection<OptionStrikeRow> Rows { get; } = [];
    public List<IvSmilePoint> SmilePoints { get; private set; } = [];

    [ObservableProperty] public partial string SymbolText { get; set; } = "";
    [ObservableProperty] public partial ExpirationChoice? SelectedExpiration { get; set; }
    [ObservableProperty] public partial StrikeWindowKind Window { get; set; } = StrikeWindowKind.Near10;
    [ObservableProperty] public partial bool ShowVolume { get; set; } = true;
    [ObservableProperty] public partial bool ShowOi { get; set; } = true;
    [ObservableProperty] public partial bool ShowIv { get; set; } = true;
    [ObservableProperty] public partial bool ShowDelta { get; set; } = true;
    [ObservableProperty] public partial bool ShowGamma { get; set; } = true;
    [ObservableProperty] public partial bool ShowTheta { get; set; } = true;
    [ObservableProperty] public partial bool ShowVega { get; set; } = true;
    [ObservableProperty] public partial bool ShowSmile { get; set; } = true;
    /// <summary>G4: spread % column (wide spreads greyed out).</summary>
    [ObservableProperty] public partial bool ShowSpread { get; set; } = true;
    /// <summary>G4: strike distance to spot column.</summary>
    [ObservableProperty] public partial bool ShowDistance { get; set; } = true;

    /// <summary>H1: the column check boxes as one value for <see cref="OptionColumns"/>.</summary>
    /// <summary>E6: compact preset — narrow columns, and volume/gamma/theta/vega switched off (they can be switched back on).</summary>
    [ObservableProperty] public partial bool CompactColumns { get; set; }

    public OptionColumnSet ColumnSet => new(ShowVolume, ShowOi, ShowIv, ShowDelta, ShowGamma, ShowTheta, ShowVega, ShowSpread, ShowDistance, CompactColumns, ShowOiChange);
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string Headline { get; set; } = "";
    /// <summary>F8: latest stock price beside the chain's (delayed, in-sync) underlying price.</summary>
    [ObservableProperty] public partial string LivePriceText { get; set; } = "";
    [ObservableProperty] public partial string DelayText { get; set; } = "";
    /// <summary>F5: "BSM (European) · r = x %" with the rate's origin.</summary>
    [ObservableProperty] public partial string ModelText { get; set; } = "";
    [ObservableProperty] public partial bool HasDelay { get; set; }
    [ObservableProperty] public partial string LocalNote { get; set; } = "";
    [ObservableProperty] public partial double? Spot { get; set; }
    [ObservableProperty] public partial bool HasRows { get; set; }
    [ObservableProperty] public partial OptionStrikeRow? AtmRow { get; set; }
    [ObservableProperty] public partial OptionStrikeRow? SelectedRow { get; set; }

    public event EventHandler? TableChanged;
    private readonly SettingsStore _settings;
    private readonly VolatilityInsights _insights;

    public OptionsViewModel(WatchlistStore watchlist, IMarketDataService data, LocalizationService loc, SymbolContext context,
        PricingAssumptions pricing, SettingsStore settings, VolatilityInsights insights)
    {
        _watchlist = watchlist; _data = data; _loc = loc; _context = context; _pricing = pricing; _settings = settings; _insights = insights;
        CompactColumns = settings.Current.Options.CompactColumns;
        _inputs = pricing.Initial;
        ModelText = PricingText.Model(_inputs.Rate, loc);
        // D1: follow the shared symbol while visible; when hidden, ActivateAsync adopts it on the next visit.
        _context.Changed += (_, sym) => { if (_active && _context.ShouldAdopt(SymbolText)) _ = AdoptContextAsync(); };
        SymbolText = watchlist.AllSymbols.Contains("AAPL", StringComparer.OrdinalIgnoreCase)
            ? "AAPL"
            : (watchlist.AllSymbols.FirstOrDefault() ?? "AAPL");
        RebuildWatchSymbols();
        _timer.Tick += async (_, _) => await LoadAsync(refreshOnly: true);
        _watchlist.Changed += (_, _) => RebuildWatchSymbols();
        _loc.LanguageChanged += (_, _) =>
        {
            ModelText = PricingText.Model(_inputs.Rate, _loc);
            RelabelExpirations();
            RelabelHeader();
            RelabelIvRank();
            UpdateStatus();
            TableChanged?.Invoke(this, EventArgs.Empty);
            if (_active && Rows.Count > 0) _ = LoadAsync(refreshOnly: true); // G2: volume/OI units follow the language
        };
    }

    public async Task ActivateAsync()
    {
        _active = true;
        if (_context.ShouldAdopt(SymbolText)) { await AdoptContextAsync(); return; }
        if (Rows.Count == 0) await LoadAsync();
        else _timer.Start();
    }

    /// <summary>D1: switch to the shared symbol (set by another page, the watchlist, or a toast) and load it.</summary>
    private Task AdoptContextAsync()
    {
        SymbolText = _context.Current!;
        return LoadAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _timer.Stop();
        _cts?.Cancel();
    }

    private void RebuildWatchSymbols()
    {
        WatchSymbols.Clear();
        foreach (var s in _watchlist.AllSymbols) WatchSymbols.Add(s);
    }

    [RelayCommand]
    private Task LoadFromBoxAsync() => LoadAsync();

    /// <summary>D1: right-click a strike → open the strategy page with that contract as the first leg.</summary>
    /// <param name="kind">"LC" buy call, "SC" sell call, "LP" buy put, "SP" sell put.</param>
    [RelayCommand]
    private void BuildStrategy(string? kind)
    {
        if (BuildSeed(kind) is not { } seed) return;
        AppServices.Get<StrategyViewModel>().Seed(seed);
        AppServices.Get<NavigationHub>().Go(AppPage.Strategy, seed.Symbol);
    }

    public StrategySeed? BuildSeed(string? kind)
    {
        if (SelectedRow is not { } row || _loadedSymbol is null) return null;
        var exp = SelectedExpiration?.Date;
        if (exp is null) return null;
        var (right, side) = kind switch
        {
            "LC" => (CRight.Call, PositionSide.Long),
            "SC" => (CRight.Call, PositionSide.Short),
            "LP" => (CRight.Put, PositionSide.Long),
            "SP" => (CRight.Put, PositionSide.Short),
            _ => ((CRight?)null, PositionSide.Long),
        };
        return right is { } r ? new StrategySeed(_loadedSymbol, exp.Value, r, side, row.Strike) : null;
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refreshOnly: true);

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Rows.Count == 0)
        {
            StatusText = _loc["Ex_NoData"];
            return;
        }
        var exp = SelectedExpiration?.Date.ToString("yyyy-MM-dd") ?? "chain";
        var headers = new[]
        {
            _loc["Op_Strike"],
            "Call " + _loc["Op_Bid"], "Call " + _loc["Op_Ask"], "Call " + _loc["Op_Last"],
            "Call " + _loc["Op_Volume"], "Call " + _loc["Op_OI"], "Call " + _loc["Op_IV"],
            "Call " + _loc["Op_Delta"], "Call " + _loc["Op_Gamma"], "Call " + _loc["Op_Theta"], "Call " + _loc["Op_Vega"],
            "Put " + _loc["Op_Bid"], "Put " + _loc["Op_Ask"], "Put " + _loc["Op_Last"],
            "Put " + _loc["Op_Volume"], "Put " + _loc["Op_OI"], "Put " + _loc["Op_IV"],
            "Put " + _loc["Op_Delta"], "Put " + _loc["Op_Gamma"], "Put " + _loc["Op_Theta"], "Put " + _loc["Op_Vega"],
        };
        var table = new ExportTable
        {
            Name = $"{SymbolText}-{exp}",
            Headers = headers,
            Rows = Rows.Select(r => (IReadOnlyList<string>)
            [
                r.StrikeText,
                r.Call?.Bid ?? "", r.Call?.Ask ?? "", r.Call?.Last ?? "", EnrichedSide.Raw(r.Call?.VolumeValue), EnrichedSide.Raw(r.Call?.OiValue), r.Call?.Iv ?? "",
                r.Call?.Delta ?? "", r.Call?.Gamma ?? "", r.Call?.Theta ?? "", r.Call?.Vega ?? "",
                r.Put?.Bid ?? "", r.Put?.Ask ?? "", r.Put?.Last ?? "", EnrichedSide.Raw(r.Put?.VolumeValue), EnrichedSide.Raw(r.Put?.OiValue), r.Put?.Iv ?? "",
                r.Put?.Delta ?? "", r.Put?.Gamma ?? "", r.Put?.Theta ?? "", r.Put?.Vega ?? "",
            ]).ToList(),
            Metadata = ExportUi.Metadata(_loc, _lastChain, _inputs.Rate, Headline),
        };
        var msg = await ExportUi.SaveAsync($"{SymbolText}-{exp}", [table], _loc);
        if (msg is not null) StatusText = msg;
    }

    [RelayCommand]
    private void SelectWindow(StrikeWindowKind kind)
    {
        Window = kind;
        ApplyWindow();
        TableChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedExpirationChanged(ExpirationChoice? value)
    {
        if (_suppressExpChange || value is null || !_active) return;
        _ = LoadAsync(refreshOnly: true);
    }

    partial void OnShowVolumeChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowOiChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowIvChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowDeltaChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowGammaChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowThetaChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowVegaChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowSmileChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowSpreadChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowDistanceChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowOiChangeChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowTermChanged(bool value) => TableChanged?.Invoke(this, EventArgs.Empty);

    partial void OnCompactColumnsChanged(bool value)
    {
        ShowVolume = ShowGamma = ShowTheta = ShowVega = ShowOiChange = !value;
        if (_settings.Current.Options.CompactColumns != value) _settings.Update(s => s.Options.CompactColumns = value);
        TableChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAsync(bool refreshOnly = false)
    {
        if (!WatchlistStore.TryNormalizeSymbol(SymbolText, out var symbol, out _))
        {
            if (!refreshOnly)
            {
                _lastError = _loc["Op_InvalidSymbol"];
                ClearTable();
                UpdateStatus();
            }
            return;
        }
        SymbolText = symbol;
        _context.Set(symbol);
        IsBusy = true;
        if (!refreshOnly || Rows.Count == 0) StatusText = _loc["Op_Loading"];
        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = _cts.Token;
        try
        {
            // A different symbol's expiration list does not apply: start from its nearest expiry.
            var exp = string.Equals(symbol, _loadedSymbol, StringComparison.OrdinalIgnoreCase) ? SelectedExpiration?.Date : null;
            var chainTask = _data.GetChainAsync(symbol, exp, ct);
            var quoteTask = SafeQuotes(symbol, ct);
            var pricingTask = _pricing.ForAsync(symbol, "options", ct);
            var chain = await chainTask;
            _loadedSymbol = symbol;
            _lastChain = chain;
            var quotes = await quoteTask;
            var q = quotes.FirstOrDefault(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            // F8: the chain's own underlying price (same moment as the option quotes) drives IV/Greeks.
            var underlying = UnderlyingText.Describe(chain, q, _loc, DateTimeOffset.Now);
            var spot = underlying.Spot ?? 0;
            Spot = underlying.Spot;

            _suppressExpChange = true;
            try
            {
                var keep = chain.Expiration ?? exp;
                Expirations.Clear();
                foreach (var d in chain.Expirations)
                    Expirations.Add(new ExpirationChoice(d, FormatExp(d)));
                SelectedExpiration = Expirations.FirstOrDefault(e => e.Date == keep) ?? Expirations.FirstOrDefault();
            }
            finally { _suppressExpChange = false; }

            _inputs = await pricingTask;
            ModelText = PricingText.Model(_inputs.Rate, _loc);
            var settings = _inputs.Enrichment(DateTimeOffset.Now, chain);
            var usedLocal = false;
            var prevOi = OpenInterestBefore(symbol, chain);
            var calls = new Dictionary<double, EnrichedSide>();
            var puts = new Dictionary<double, EnrichedSide>();
            foreach (var c in chain.Contracts)
            {
                if (SelectedExpiration is { } se && c.Expiration != se.Date) continue;
                var e = OptionEnricher.Enrich(
                    c.Right == CRight.Call ? ARight.Call : ARight.Put,
                    spot, c.Strike, c.Expiration,
                    c.ImpliedVolatility, c.Bid, c.Ask, c.Last,
                    c.Delta, c.Gamma, c.Theta, c.Vega, c.Rho, c.TheoreticalPrice,
                    c.InTheMoney, settings);
                if (e.GreeksComputedLocally || e.IvOrigin is IvOrigin.SolvedFromMid or IvOrigin.SolvedFromLast)
                    usedLocal = true;
                var side = EnrichedSide.From(c, e, _loc, prevOi.TryGetValue(c.ContractSymbol, out var before) ? before : null);
                var bag = c.Right == CRight.Call ? calls : puts;
                bag[c.Strike] = side;
            }

            var strikes = calls.Keys.Concat(puts.Keys).Distinct().OrderBy(x => x).ToList();
            var atm = spot > 0 && strikes.Count > 0
                ? strikes.MinBy(k => Math.Abs(k - spot))
                : (double?)null;
            var rows = new List<OptionStrikeRow>(strikes.Count);
            foreach (var k in strikes)
            {
                calls.TryGetValue(k, out var call);
                puts.TryGetValue(k, out var put);
                rows.Add(new OptionStrikeRow(k, call, put, isAtm: atm is { } a && k == a, spot: Spot));
            }
            OptionStrikeRow.SetOiRatios(rows);
            _allRows = rows;
            ApplyWindow();
            BuildSmile(rows);
            AtmRow = Rows.FirstOrDefault(r => r.IsAtm) ?? _allRows.FirstOrDefault(r => r.IsAtm);

            _lastError = Rows.Count == 0 ? _loc["Op_Empty"] : null;
            (_hdrSymbol, _hdrQuote, _hdrLocal) = (symbol, q, usedLocal);
            RelabelHeader();
            if (!refreshOnly) _ = LoadInsightsAsync(symbol, chain.Expirations, spot, ct);
        }
        catch (AllSourcesFailedException) { Fail(_loc["Op_AllFailed"], refreshOnly); }
        catch (OperationCanceledException) when (_active && !refreshOnly) { Fail(_loc["Op_Timeout"], refreshOnly); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("options", "load failed", ex); Fail(ex.Message, refreshOnly); }
        finally
        {
            IsBusy = false;
            if (_active) _timer.Start();
            UpdateStatus();
            TableChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Fail(string message, bool refreshOnly)
    {
        _lastError = message;
        if (!refreshOnly || Rows.Count == 0) ClearTable();
    }

    private void ClearTable()
    {
        _allRows = [];
        Rows.Clear();
        SmilePoints = [];
        HasRows = false;
        AtmRow = null;
        _hdrSymbol = null;
        Headline = "";
        LivePriceText = "";
        DelayText = "";
        HasDelay = false;
        LocalNote = "";
    }

    private void ApplyWindow()
    {
        IEnumerable<OptionStrikeRow> src = _allRows;
        if (Window != StrikeWindowKind.All && _allRows.Count > 0)
        {
            var atmIndex = _allRows.ToList().FindIndex(r => r.IsAtm);
            if (atmIndex < 0) atmIndex = _allRows.Count / 2;
            var n = (int)Window;
            var lo = Math.Max(0, atmIndex - n);
            var hi = Math.Min(_allRows.Count - 1, atmIndex + n);
            src = _allRows.Skip(lo).Take(hi - lo + 1);
        }
        Rows.Clear();
        foreach (var r in src) Rows.Add(r);
        HasRows = Rows.Count > 0;
        AtmRow = Rows.FirstOrDefault(r => r.IsAtm) ?? _allRows.FirstOrDefault(r => r.IsAtm);
    }

    private void BuildSmile(IReadOnlyList<OptionStrikeRow> rows)
    {
        var pts = new List<IvSmilePoint>();
        foreach (var r in rows)
        {
            if (r.Call?.IvValue is { } civ) pts.Add(new IvSmilePoint(r.Strike, civ, true));
            if (r.Put?.IvValue is { } piv) pts.Add(new IvSmilePoint(r.Strike, piv, false));
        }
        SmilePoints = pts;
    }

    private void RelabelExpirations()
    {
        var keep = SelectedExpiration?.Date;
        _suppressExpChange = true;
        try
        {
            var dates = Expirations.Select(e => e.Date).ToList();
            Expirations.Clear();
            foreach (var d in dates) Expirations.Add(new ExpirationChoice(d, FormatExp(d)));
            SelectedExpiration = Expirations.FirstOrDefault(e => e.Date == keep) ?? Expirations.FirstOrDefault();
        }
        finally { _suppressExpChange = false; }
    }

    private string FormatExp(DateOnly d)
    {
        var dte = OptionTime.DaysToExpiry(d, DateTimeOffset.Now);
        var days = string.Format(_loc["Op_Dte"], dte);
        return $"{d:yyyy-MM-dd}  ·  {days}";
    }

    /// <summary>E4: header/status texts from the last load, rebuilt in the current language.</summary>
    private void RelabelHeader()
    {
        if (_hdrSymbol is not { } symbol) return;
        var now = DateTimeOffset.Now;
        var u = UnderlyingText.Describe(_lastChain, _hdrQuote, _loc, now);
        Headline = u.Main.Length > 0 ? $"{symbol}   {u.Main}" : symbol;
        LivePriceText = u.Live;
        DelayText = _lastChain is { } c ? FreshnessText.ChainTiming(c, _loc, now) : "";
        HasDelay = DelayText.Length > 0;
        LocalNote = _hdrLocal ? string.Format(_loc["Op_LocalGreeks"], _inputs.Rate.Rate) : "";
        if (_lastError is null && _lastChain is { } ch)
            StatusText = string.Format(_loc["Op_Source"], SourceNames.Display(ch.Source, _loc), Rows.Count);
    }

    private void UpdateStatus()
    {
        if (_lastError is not null) StatusText = _lastError;
    }

    private Task<IReadOnlyList<Quote>> SafeQuotes(string symbol, CancellationToken ct) => SafeData.QuotesAsync(_data, symbol, "options", ct);
}

public sealed record ExpirationChoice(DateOnly Date, string Label)
{
    public override string ToString() => Label;
}

public sealed record IvSmilePoint(double Strike, double Iv, bool IsCall);

public sealed class OptionStrikeRow
{
    public double Strike { get; }
    public string StrikeText { get; }
    public EnrichedSide? Call { get; }
    public EnrichedSide? Put { get; }
    public bool IsAtm { get; }
    public bool CallItm { get; }
    public bool PutItm { get; }
    /// <summary>G4: (strike − spot) / spot, e.g. "+2.3%".</summary>
    public double? Distance { get; }
    public string DistanceText { get; } = "—";

    /// <summary>G4: scale each side's OI data bar to the largest OI on that side of the chain.</summary>
    public static void SetOiRatios(IReadOnlyList<OptionStrikeRow> rows)
    {
        var maxCall = rows.Max(r => r.Call?.OiValue ?? 0);
        var maxPut = rows.Max(r => r.Put?.OiValue ?? 0);
        foreach (var r in rows)
        {
            if (r.Call is { } c) c.OiRatio = maxCall > 0 ? Math.Clamp((c.OiValue ?? 0) / maxCall, 0, 1) : 0;
            if (r.Put is { } p) p.OiRatio = maxPut > 0 ? Math.Clamp((p.OiValue ?? 0) / maxPut, 0, 1) : 0;
        }
    }

    public OptionStrikeRow(double strike, EnrichedSide? call, EnrichedSide? put, bool isAtm, double? spot)
    {
        Strike = strike;
        StrikeText = strike.ToString(strike == Math.Truncate(strike) ? "0" : "0.00", CultureInfo.InvariantCulture);
        Call = call;
        Put = put;
        IsAtm = isAtm;
        CallItm = call?.InTheMoney ?? (spot is { } s && s > strike);
        PutItm = put?.InTheMoney ?? (spot is { } s2 && s2 < strike);
        if (spot is > 0 and var sp)
        {
            Distance = (strike - sp) / sp;
            DistanceText = (Distance.Value * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
        }
    }
}

public sealed class EnrichedSide
{
    public string Bid { get; init; } = "—";
    public string Ask { get; init; } = "—";
    public string Last { get; init; } = "—";
    public string Volume { get; init; } = "—";
    public string Oi { get; init; } = "—";
    public string Iv { get; init; } = "—";
    public string Delta { get; init; } = "—";
    public string Gamma { get; init; } = "—";
    public string Theta { get; init; } = "—";
    public string Vega { get; init; } = "—";
    public double? IvValue { get; init; }
    /// <summary>G2: raw numbers for export (the display text uses 万/K units).</summary>
    public double? VolumeValue { get; init; }
    public double? OiValue { get; init; }
    /// <summary>G4: bid/ask spread and its share of the mid price.</summary>
    public string Spread { get; init; } = "—";
    public string SpreadPct { get; init; } = "—";
    public bool IsWideSpread { get; init; }
    /// <summary>G4: 0..1 of the largest OI on this side (data bar width); set after the chain is built.</summary>
    public double OiRatio { get; set; }

    /// <summary>G4: a spread is "wide" above this share of mid (and wider than <see cref="WideSpreadMinAbs"/>).</summary>
    public const double WideSpreadPct = 0.10;
    public const double WideSpreadMinAbs = 0.05;

    public static (double? Spread, double? Pct, bool Wide) SpreadOf(double? bid, double? ask)
    {
        if (bid is not > 0 || ask is not > 0 || ask < bid) return (null, null, false);
        var spread = ask.Value - bid.Value;
        var mid = (ask.Value + bid.Value) / 2;
        var pct = spread / mid;
        return (spread, pct, pct > WideSpreadPct && spread > WideSpreadMinAbs);
    }
    public bool InTheMoney { get; init; }
    public bool IvLocal { get; init; }
    public string IvHint { get; init; } = "";

    /// <summary>B6: open interest change since the previous stored day ("—" without one).</summary>
    public string OiChange { get; init; } = "—";
    public double? OiChangeValue { get; init; }

    public static EnrichedSide From(OptionContract c, EnrichedOption e, LocalizationService loc, double? previousOi = null)
    {
        var oiChange = c.OpenInterest is { } oiNow && previousOi is { } oiThen ? oiNow - oiThen : (double?)null;
        var ivLocal = e.IvOrigin is IvOrigin.SolvedFromMid or IvOrigin.SolvedFromLast;
        var (spread, pct, wide) = SpreadOf(c.Bid, c.Ask);
        return new EnrichedSide
        {
            Spread = spread is { } sp ? NumberDisplay.Price(sp) : "—",
            SpreadPct = pct is { } pc ? (pc * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—",
            IsWideSpread = wide,
            Bid = Px(c.Bid), Ask = Px(c.Ask), Last = Px(c.Last),
            Volume = Qty(c.Volume, loc), Oi = Qty(c.OpenInterest, loc), VolumeValue = c.Volume, OiValue = c.OpenInterest,
            Iv = e.ImpliedVolatility is { } iv ? (iv * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—",
            Delta = G(e.Delta, "0.000"), Gamma = G(e.Gamma, "0.0000"),
            Theta = G(e.Theta, "0.00"), Vega = G(e.Vega, "0.00"),
            IvValue = e.ImpliedVolatility, InTheMoney = e.InTheMoney, IvLocal = ivLocal,
            IvHint = ivLocal ? loc["Op_IvLocal"] : "",
            OiChangeValue = oiChange,
            OiChange = oiChange is { } d ? (d > 0 ? "+" : d < 0 ? "−" : "") + (d == 0 ? "0" : loc.Amount(Math.Abs(d))) : "—",
        };
    }

    private static string Px(double? v) => v is { } x && x > 0 ? NumberDisplay.Price(x) : "—";
    private static string Qty(double? v, LocalizationService loc) => v is { } x && x > 0 ? loc.Amount(x) : "—";
    internal static string Raw(double? v) => v is { } x && double.IsFinite(x) ? x.ToString("0", CultureInfo.InvariantCulture) : "";
    private static string G(double? v, string fmt) => v is { } x && double.IsFinite(x) ? x.ToString(fmt, CultureInfo.InvariantCulture) : "—";
}
