using Meili.Core.Diagnostics;
using Meili.Automation;
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

namespace Meili.Shared.ViewModels;

public enum ChartRangeKind { Session, Minute1, Minute5, Minute15, Hour1, Day1, Week1, Month1 }

public partial class ChartRangeTab : ObservableObject
{
    public ChartRangeKind Kind { get; }
    public string Key { get; }
    public string CompactTitle { get; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public ChartRangeTab(ChartRangeKind kind, string key, string title, bool selected, string? compactTitle = null)
    {
        Kind = kind; Key = key; Title = title; CompactTitle = compactTitle ?? title; IsSelected = selected;
    }
}

/// <summary>K-line page: symbol + interval, live-enough bar refresh, indicator toggles, snapshot for the plotter.</summary>
public partial class ChartsViewModel : ObservableObject
{
    private readonly WatchlistStore _watchlist;
    private readonly IMarketDataService _data;
    private readonly LocalizationService _loc;
    private readonly SymbolContext _context;
    private readonly SettingsStore _settings;
    private (string Symbol, BarInterval Interval, string Source, PriceAdjustment Basis)? _lastBasis;
    private readonly IUiTimer _timer = Ui.Timer();
    private CancellationTokenSource? _cts;
    private bool _active;

    // Desktop presentation only: never changes chart calculations or MCP permissions.
    [ObservableProperty] public partial bool ShowInspector { get; set; }

    public ObservableCollection<string> WatchSymbols { get; } = [];
    public ObservableCollection<ChartRangeTab> Ranges { get; } = [];

    [ObservableProperty] public partial string SymbolText { get; set; } = "";
    [ObservableProperty] public partial ChartRangeKind Range { get; set; } = ChartRangeKind.Day1;
    [ObservableProperty] public partial bool ShowMa { get; set; } = true;
    [ObservableProperty] public partial bool ShowEma { get; set; } = true;
    [ObservableProperty] public partial bool ShowBoll { get; set; }
    [ObservableProperty] public partial bool ShowVolume { get; set; } = true;
    [ObservableProperty] public partial bool ShowMacd { get; set; }
    [ObservableProperty] public partial bool ShowRsi { get; set; }
    [ObservableProperty] public partial bool ShowKdj { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string CrosshairText { get; set; } = "";
    [ObservableProperty] public partial string Headline { get; set; } = "";
    [ObservableProperty] public partial BarSeries? Series { get; set; }
    [ObservableProperty] public partial KlineSnapshot? Snapshot { get; set; }

    // G3: indicator parameters (doubles because the number boxes edit doubles; normalised on change).
    [ObservableProperty] public partial double MaPeriod { get; set; }
    [ObservableProperty] public partial double EmaPeriod { get; set; }
    [ObservableProperty] public partial double BollPeriod { get; set; }
    [ObservableProperty] public partial double BollWidth { get; set; }
    [ObservableProperty] public partial double MacdFast { get; set; }
    [ObservableProperty] public partial double MacdSlow { get; set; }
    [ObservableProperty] public partial double MacdSignal { get; set; }
    [ObservableProperty] public partial double RsiPeriod { get; set; }
    [ObservableProperty] public partial double KdjPeriod { get; set; }
    [ObservableProperty] public partial double KdjM1 { get; set; }
    [ObservableProperty] public partial double KdjM2 { get; set; }
    [ObservableProperty] public partial string MaLabel { get; set; } = "";
    [ObservableProperty] public partial string EmaLabel { get; set; } = "";
    [ObservableProperty] public partial string BollLabel { get; set; } = "";
    [ObservableProperty] public partial string MacdLabel { get; set; } = "";
    [ObservableProperty] public partial string RsiLabel { get; set; } = "";
    [ObservableProperty] public partial string KdjLabel { get; set; } = "";
    private IndicatorSettings _ind = new();

    // B5: user drawings (per symbol, saved) and one rebased comparison symbol.
    public bool IsHorizontalDrawing => DrawMode == DrawingKind.HorizontalLine;
    public bool IsTrendDrawing => DrawMode == DrawingKind.TrendLine;
    [NotifyPropertyChangedFor(nameof(IsHorizontalDrawing), nameof(IsTrendDrawing))]
    [ObservableProperty] public partial DrawingKind? DrawMode { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ChartDrawing> Drawings { get; set; } = [];
    [ObservableProperty] public partial string CompareText { get; set; } = "";
    [ObservableProperty] public partial string CompareStatus { get; set; } = "";
    /// <summary>Comparison closes rescaled so its first shared bar starts at the main symbol's close.</summary>
    public (DateTimeOffset[] Time, double[] Close)? CompareLine { get; private set; }
    public string CompareLabel { get; private set; } = "";
    private (DateTimeOffset Time, double Price)? _trendStart;
    private readonly ChartDrawingStore _drawings;
    private bool _loadingInd;
    private OhlcvBar[] _bars = [];
    private bool[] _incomplete = [];
    /// <summary>F7: request dividend-adjusted (adjclose) bars; persisted.</summary>
    [ObservableProperty] public partial bool DividendAdjusted { get; set; }
    /// <summary>F7: basis of the bars on screen, e.g. "拆股调整 · 不含分红".</summary>
    [ObservableProperty] public partial string AdjustText { get; set; } = "";
    /// <summary>F7: shown when the source cannot give what was asked, or a source switch changed the basis.</summary>
    [ObservableProperty] public partial string AdjustWarning { get; set; } = "";

    public MarketTimeline? Timeline { get; private set; }
    private CancellationTokenSource? _timelineCts;
    private string? _timelineSymbol;
    private DateTimeOffset _timelineRequested;
    private async Task RefreshTimelineAsync(string symbol, bool callerWillRender = false)
    {
        var loading = LoadTimelineAsync(symbol);
        var completedSynchronously = loading.IsCompleted;
        if (await loading && (!callerWillRender || !completedSynchronously)) PlotChanged?.Invoke(this, EventArgs.Empty);
    }
    private async Task<bool> LoadTimelineAsync(string symbol)
    {
        if (_timelineSymbol == symbol && DateTimeOffset.UtcNow - _timelineRequested < TimeSpan.FromMinutes(5)) return false;
        _timelineCts?.Cancel();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        _timelineCts = cancellation; _timelineSymbol = symbol; _timelineRequested = DateTimeOffset.UtcNow;
        if (Timeline?.Symbol != symbol) Timeline = null;
        try
        {
            var result = await MarketTimelineService.LoadAsync(_data, symbol, limit: 100, ct: cancellation.Token);
            if (cancellation.IsCancellationRequested || _timelineCts != cancellation || Series?.Symbol != symbol) return false;
            Timeline = result;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (_timelineCts != cancellation) return false;
            _timelineSymbol = null;
            if (Series?.Symbol != symbol) return false;
            Timeline = new(symbol, [], new[] { "news", "earnings", "ex_dividend" }.Select(t => new EventCoverage(t, "unavailable", "provider_timeout")).ToArray(), "unavailable", DateTimeOffset.UtcNow, false, 0);
            return true;
        }
        finally { cancellation.Dispose(); if (_timelineCts == cancellation) _timelineCts = null; }
    }
    public void OpenTimelineEvent(string id)
    {
        if (Timeline?.Symbol != Series?.Symbol || SymbolText != Series?.Symbol) return;
        var item = Timeline?.Events.FirstOrDefault(e => e.Id == id && e.Type == "news");
        if (item?.Url is { } url && NewsReaderPolicy.IsAllowed(url)) Ui.Platform.OpenNewsArticle(url, item.Title);
    }
    public event EventHandler? PlotChanged;

    /// <summary>
    /// C2: false right after a symbol/range load (the view should fit the new data); true for timer refreshes and
    /// indicator toggles, where the user's zoom/pan must be kept.
    /// </summary>
    public bool PreserveView { get; private set; } = true;

    /// <summary>C2: count of refreshes that brought nothing new and were not re-rendered (diagnostics/tests).</summary>
    public int UnchangedRefreshes { get; private set; }

    /// <summary>Same bars as already shown (count, first and last bar, source): a refresh with nothing new.</summary>
    internal static bool SameData(BarSeries a, BarSeries b) =>
        a.Bars.Count == b.Bars.Count && a.Bars.Count > 0
        && string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase)
        && a.OfflineCache == b.OfflineCache
        && a.Bars[0].Time == b.Bars[0].Time
        && a.Bars[^1] == b.Bars[^1];

    public ChartsViewModel(WatchlistStore watchlist, IMarketDataService data, LocalizationService loc, SymbolContext context, SettingsStore settings,
        ChartDrawingStore drawings)
    {
        _watchlist = watchlist; _data = data; _loc = loc; _context = context; _settings = settings; _drawings = drawings;
        ApplyIndicators(settings.Current.Charts.Indicators.Normalized());
        DividendAdjusted = settings.Current.Charts.DividendAdjusted;
        // D1: follow the shared symbol while visible; when hidden, ActivateAsync adopts it on the next visit.
        _context.Changed += (_, sym) => { if (_active && _context.ShouldAdopt(SymbolText)) _ = AdoptContextAsync(); };
        SymbolText = watchlist.AllSymbols.FirstOrDefault() ?? "AAPL";
        RebuildWatchSymbols();
        RebuildRanges();
        _timer.Tick += async (_, _) => await LoadAsync(refreshOnly: true);
        _watchlist.Changed += (_, _) => RebuildWatchSymbols();
        _loc.LanguageChanged += (_, _) =>
        {
            RebuildRanges();
            UpdateStatus();
            if (Series is { } series) AdjustText = FreshnessText.Adjustment(series.Adjustment, _loc); // E4
            PlotChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public async Task ActivateAsync()
    {
        _active = true;
        if (_context.ShouldAdopt(SymbolText)) { await AdoptContextAsync(); return; }
        if (Series is null) await LoadAsync();
        else
        {
            _ = RefreshTimelineAsync(Series.Symbol);
            ArmTimer();
            _timer.Start();
        }
    }

    /// <summary>D1: switch to the shared symbol (set by another page, the watchlist, or a toast) and load it.</summary>
    private Task AdoptContextAsync()
    {
        SymbolText = _context.Current!;
        return LoadAsync();
    }

    public void Deactivate()
    {
        _active = false; DrawingEpoch++; DrawMode = null; _trendStart = null;
        _timer.Stop();
        _cts?.Cancel();
        var timelineCancellation = _timelineCts;
        _timelineCts = null; _timelineSymbol = null;
        timelineCancellation?.Cancel();
    }

    public Task OpenSymbolAsync(string symbol)
    {
        if (WatchlistStore.TryNormalizeSymbol(symbol, out var s, out _))
            SymbolText = s;
        else if (!string.IsNullOrWhiteSpace(symbol))
            SymbolText = symbol.Trim().ToUpperInvariant();
        return LoadAsync();
    }

    private void RebuildWatchSymbols()
    {
        WatchSymbols.Clear();
        foreach (var s in _watchlist.AllSymbols) WatchSymbols.Add(s);
    }

    private void RebuildRanges()
    {
        var keep = Range;
        Ranges.Clear();
        foreach (var (kind, key) in AllRanges())
            Ranges.Add(new ChartRangeTab(kind, key, _loc[key], kind == keep, _loc[key+"_Short"]));
    }

    private static (ChartRangeKind kind, string key)[] AllRanges() =>
    [
        (ChartRangeKind.Session, "Ch_Session"),
        (ChartRangeKind.Minute1, "Ch_1m"),
        (ChartRangeKind.Minute5, "Ch_5m"),
        (ChartRangeKind.Minute15, "Ch_15m"),
        (ChartRangeKind.Hour1, "Ch_1h"),
        (ChartRangeKind.Day1, "Ch_1d"),
        (ChartRangeKind.Week1, "Ch_1w"),
        (ChartRangeKind.Month1, "Ch_1mo"),
    ];

    [RelayCommand]
    private void SelectRange(ChartRangeKind kind)
    {
        if (Range == kind) return;
        Range = kind;
        foreach (var r in Ranges) r.IsSelected = r.Kind == kind;
        _ = LoadAsync();
    }

    [RelayCommand]
    private Task LoadFromBoxAsync() => LoadAsync();

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refreshOnly: true);

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Series is null || Series.Bars.Count == 0)
        {
            StatusText = _loc["Ex_NoData"];
            return;
        }
        var headers = new[] { _loc["Ex_Time"], "Open", "High", "Low", "Close", _loc["Ch_Volume"], _loc["Ex_Incomplete"] };
        var meta = ExportUi.Metadata(_loc, Series).Append(new(_loc["Ex_MetaAdjust"], FreshnessText.Adjustment(Series.Adjustment, _loc))).ToList();
        var table = MarketExport.Bars($"{SymbolText}-{Range}", headers, Series) with { Metadata = meta };
        var msg = await ExportUi.SaveAsync($"{SymbolText}-{Range}", [table], _loc);
        if (msg is not null) StatusText = msg;
    }

    partial void OnShowMaChanged(bool value) => PlotChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowEmaChanged(bool value) => PlotChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowBollChanged(bool value) => PlotChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowVolumeChanged(bool value) => PlotChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowMacdChanged(bool value) => PlotChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowRsiChanged(bool value) => PlotChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowKdjChanged(bool value) => PlotChanged?.Invoke(this, EventArgs.Empty);

    public async Task LoadAsync(bool refreshOnly = false)
    {
        if (!WatchlistStore.TryNormalizeSymbol(SymbolText, out var symbol, out _))
        {
            if (!refreshOnly)
            {
                StatusText = _loc["Ch_InvalidSymbol"];
                _timelineCts?.Cancel(); Timeline = null; _timelineSymbol = null;
                Series = null;
                Snapshot = null;
                PlotChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        SymbolText = symbol;
        _context.Set(symbol);
        if (IsBusy) { _cts?.Cancel(); }
        IsBusy = true;
        if (!refreshOnly || Series is null) StatusText = _loc["Ch_Loading"];
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var unchanged = false;
        try
        {
            var request = BuildRequest(symbol, Range) with { Adjust = DividendAdjusted ? AdjustMode.Total : AdjustMode.Split };
            // C2: the cache layer turns a refresh into a short tail download; nothing new → no recompute, no redraw.
            var series = await _data.GetBarsAsync(request, _cts.Token);

            if (refreshOnly && Series is { } shown && SameData(shown, series))
            {
                _ = RefreshTimelineAsync(symbol);
                unchanged = true;
                UnchangedRefreshes++;
                return;
            }
            var bars = series.Bars.Select(b => new OhlcvBar(b.Time, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();
            if (!refreshOnly || Series is null || Series.Symbol != series.Symbol || Series.Interval != series.Interval || Series.Source != series.Source || Series.Adjustment != series.Adjustment)
            { DrawingEpoch++; DrawMode = null; _trendStart = null; }
            Series = series;
            DescribeAdjustment(request, series);
            _bars = bars;
            _incomplete = series.Bars.Select(b => b.Incomplete || !b.IsConsistent()).ToArray();
            Snapshot = bars.Length == 0 ? null : KlineSnapshot.Compute(bars, IndicatorOptions(_ind), _incomplete);
            Drawings = _drawings.For(symbol);
            await LoadCompareAsync(request, series, _cts.Token);
            CrosshairText = "";
            Headline = string.IsNullOrWhiteSpace(symbol) ? "" : symbol;
            _lastError = series.Bars.Count == 0 ? _loc["Ch_Empty"] : null;
        }
        catch (AllSourcesFailedException) { if (!refreshOnly || Series is null) Fail(_loc["Ch_AllFailed"]); }
        catch (OperationCanceledException) when (_active && !refreshOnly) { Fail(_loc["Ch_Timeout"]); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("charts", "load failed", ex); Fail(ex.Message); }
        finally
        {
            IsBusy = false;
            ArmTimer();
            if (_active) _timer.Start();
            UpdateStatus();
            if (!unchanged)
            {
                PreserveView = refreshOnly;
                if (Series?.Symbol == symbol) _ = RefreshTimelineAsync(symbol, callerWillRender: true);
                PlotChanged?.Invoke(this, EventArgs.Empty);
                PreserveView = true;
            }
        }
    }

    private string? _lastError;

    partial void OnDividendAdjustedChanged(bool value)
    {
        if (_settings.Current.Charts.DividendAdjusted != value) _settings.Update(s => s.Charts.DividendAdjusted = value);
        if (Series is not null) _ = LoadAsync();
    }

    [RelayCommand]
    private void StartDrawing(DrawingKind kind)
    {
        DrawingEpoch++;
        DrawMode = DrawMode == kind ? null : kind;
        _trendStart = null;
        StatusText = DrawMode switch
        {
            DrawingKind.HorizontalLine => _loc["Ch_DrawHLineHint"],
            DrawingKind.TrendLine => _loc["Ch_DrawTrendHint1"],
            _ => StatusText,
        };
    }

    [RelayCommand]
    private void ClearDrawings()
    {
        if (Series is null) return;
        try { _drawings.Clear(Series.Symbol); }
        catch (DataConflictException) { ReportDrawingConflict(); return; }
        Drawings = []; DrawMode = null; _trendStart = null;
        PlotChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>B5: a click on the price chart while drawing. Returns true when the click was used for drawing.</summary>
    public bool AddDrawingPoint(DateTimeOffset time, double price)
    {
        if (DrawMode is not { } mode || Series is null) return false;
        if (mode == DrawingKind.TrendLine && _trendStart is null)
        {
            _trendStart = (time, price);
            StatusText = _loc["Ch_DrawTrendHint2"];
            PlotChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        if (mode == DrawingKind.TrendLine && _trendStart!.Value.Time == time) return false;
        var drawing = mode == DrawingKind.HorizontalLine
            ? new ChartDrawing(mode, price)
            : new ChartDrawing(mode, _trendStart!.Value.Price, _trendStart.Value.Time, price, time);
        try { _drawings.Add(Series.Symbol, drawing); }
        catch (DataConflictException) { ReportDrawingConflict(); return false; }
        Drawings = _drawings.For(Series.Symbol).ToList();
        DrawMode = null;
        _trendStart = null;
        UpdateStatus();
        PlotChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string ChartKey => $"{Series?.Symbol}|{Range}|{Series?.Source}|{Series?.Adjustment}";
    public long DrawingEpoch { get; private set; }
    public (DateTimeOffset Time, double Price)? DrawingStart => _trendStart;
    public string DrawingRevision => Series is null ? "" : _drawings.Revision(Series.Symbol);
    public void CancelDrawing()
    {
        DrawingEpoch++; DrawMode = null; _trendStart = null; UpdateStatus(); PlotChanged?.Invoke(this, EventArgs.Empty);
    }
    public void EditDrawing(int index, double? first, double? second, string revision)
    {
        if (Series is null || index < 0 || index >= Drawings.Count) return;
        var original = Drawings[index];
        try
        {
            _drawings.Edit(Series.Symbol, index, first is null ? null : original with { Price1 = first.Value, Price2 = second ?? original.Price2 }, revision);
            Drawings = _drawings.For(Series.Symbol).ToList(); PlotChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (DataConflictException) { ReportDrawingConflict(); PlotChanged?.Invoke(this, EventArgs.Empty); }
    }
    private void ReportDrawingConflict()
    {
        DrawMode = null; _trendStart = null;
        _lastError = _loc["Ch_DrawingConflict"];
        UpdateStatus();
    }

    [RelayCommand]
    private async Task ApplyCompareAsync()
    {
        if (Series is null) return;
        await LoadCompareAsync(BuildRequest(Series.Symbol, Range) with { Adjust = DividendAdjusted ? AdjustMode.Total : AdjustMode.Split }, Series, CancellationToken.None);
        PlotChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadCompareAsync(BarRequest request, BarSeries main, CancellationToken ct)
    {
        CompareLine = null;
        CompareLabel = "";
        CompareStatus = "";
        if (string.IsNullOrWhiteSpace(CompareText)) return;
        if (!WatchlistStore.TryNormalizeSymbol(CompareText, out var cmp, out _)) { CompareStatus = _loc["Ch_InvalidSymbol"]; return; }
        CompareText = cmp;
        try
        {
            var other = await _data.GetBarsAsync(request with { Symbol = cmp }, ct);
            CompareLine = Rebase(main, other);
            if (CompareLine is null) CompareStatus = string.Format(_loc["Ch_CompareNoOverlap"], cmp);
            else CompareLabel = string.Format(_loc["Ch_CompareLabel"], cmp);
        }
        catch (AllSourcesFailedException ex)
        {
            AppLog.Warn("charts", $"compare {cmp} failed", ex);
            CompareStatus = string.Format(_loc["Ch_CompareFailed"], cmp);
        }
    }

    /// <summary>B5: comparison closes on shared bar times, scaled so the first shared bar equals the main close.</summary>
    public static (DateTimeOffset[] Time, double[] Close)? Rebase(BarSeries main, BarSeries other)
    {
        var byTime = new Dictionary<DateTimeOffset, double>();
        foreach (var b in other.Bars) if (b.Close > 0) byTime[b.Time] = b.Close;
        var times = new List<DateTimeOffset>();
        var closes = new List<double>();
        double? scale = null;
        foreach (var b in main.Bars)
        {
            if (b.Close <= 0 || !byTime.TryGetValue(b.Time, out var c)) continue;
            scale ??= b.Close / c;
            times.Add(b.Time);
            closes.Add(c * scale.Value);
        }
        return times.Count < 2 ? null : (times.ToArray(), closes.ToArray());
    }

    private static KlineIndicatorOptions IndicatorOptions(IndicatorSettings i) => new()
    {
        SmaPeriod = i.MaPeriod, EmaPeriod = i.EmaPeriod, BollingerPeriod = i.BollPeriod, BollingerMultiplier = i.BollWidth,
        MacdFast = i.MacdFast, MacdSlow = i.MacdSlow, MacdSignal = i.MacdSignal, RsiPeriod = i.RsiPeriod,
        KdjPeriod = i.KdjPeriod, KdjM1 = i.KdjM1, KdjM2 = i.KdjM2,
    };

    /// <summary>G3: shows <paramref name="i"/> in the number boxes and labels, recomputes the indicators, and saves it.</summary>
    private void ApplyIndicators(IndicatorSettings i)
    {
        _ind = i;
        _loadingInd = true;
        (MaPeriod, EmaPeriod, BollPeriod, BollWidth) = (i.MaPeriod, i.EmaPeriod, i.BollPeriod, i.BollWidth);
        (MacdFast, MacdSlow, MacdSignal, RsiPeriod) = (i.MacdFast, i.MacdSlow, i.MacdSignal, i.RsiPeriod);
        (KdjPeriod, KdjM1, KdjM2) = (i.KdjPeriod, i.KdjM1, i.KdjM2);
        _loadingInd = false;
        var inv = CultureInfo.InvariantCulture;
        MaLabel = $"MA({i.MaPeriod})";
        EmaLabel = $"EMA({i.EmaPeriod})";
        BollLabel = string.Create(inv, $"BOLL({i.BollPeriod},{i.BollWidth:0.##})");
        MacdLabel = $"MACD({i.MacdFast},{i.MacdSlow},{i.MacdSignal})";
        RsiLabel = $"RSI({i.RsiPeriod})";
        KdjLabel = $"KDJ({i.KdjPeriod},{i.KdjM1},{i.KdjM2})";
        if (_settings.Current.Charts.Indicators != i) _settings.Update(s => s.Charts.Indicators = i);
        if (_bars.Length > 0) Snapshot = KlineSnapshot.Compute(_bars, IndicatorOptions(i), _incomplete);
        PlotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnIndicatorEdited()
    {
        if (_loadingInd) return;
        static int R(double v) => (int)Math.Round(v);
        ApplyIndicators(new IndicatorSettings
        {
            MaPeriod = R(MaPeriod), EmaPeriod = R(EmaPeriod), BollPeriod = R(BollPeriod), BollWidth = BollWidth,
            MacdFast = R(MacdFast), MacdSlow = R(MacdSlow), MacdSignal = R(MacdSignal), RsiPeriod = R(RsiPeriod),
            KdjPeriod = R(KdjPeriod), KdjM1 = R(KdjM1), KdjM2 = R(KdjM2),
        }.Normalized());
    }

    partial void OnMaPeriodChanged(double value) => OnIndicatorEdited();
    partial void OnEmaPeriodChanged(double value) => OnIndicatorEdited();
    partial void OnBollPeriodChanged(double value) => OnIndicatorEdited();
    partial void OnBollWidthChanged(double value) => OnIndicatorEdited();
    partial void OnMacdFastChanged(double value) => OnIndicatorEdited();
    partial void OnMacdSlowChanged(double value) => OnIndicatorEdited();
    partial void OnMacdSignalChanged(double value) => OnIndicatorEdited();
    partial void OnRsiPeriodChanged(double value) => OnIndicatorEdited();
    partial void OnKdjPeriodChanged(double value) => OnIndicatorEdited();
    partial void OnKdjM1Changed(double value) => OnIndicatorEdited();
    partial void OnKdjM2Changed(double value) => OnIndicatorEdited();

    [RelayCommand]
    private void ResetIndicators() => ApplyIndicators(new IndicatorSettings());

    /// <summary>F7: label the basis and warn about mismatches (asked vs. delivered, or a source switch).</summary>
    private void DescribeAdjustment(BarRequest request, BarSeries series)
    {
        AdjustText = FreshnessText.Adjustment(series.Adjustment, _loc);
        var warn = "";
        if (_lastBasis is { } prev && prev.Symbol == request.Symbol && prev.Interval == request.Interval
            && prev.Basis != series.Adjustment && !string.Equals(prev.Source, series.Source, StringComparison.OrdinalIgnoreCase))
            warn = string.Format(_loc["Ch_AdjChanged"], SourceNames.Display(prev.Source, _loc), SourceNames.Display(series.Source, _loc),
                FreshnessText.Adjustment(prev.Basis, _loc), FreshnessText.Adjustment(series.Adjustment, _loc));
        else if (request.Adjust == AdjustMode.Total && !request.Interval.IsIntraday() && series.Adjustment != PriceAdjustment.SplitDividend)
            warn = string.Format(_loc["Ch_AdjNotOffered"], SourceNames.Display(series.Source, _loc), AdjustText);
        AdjustWarning = warn;
        _lastBasis = (request.Symbol, request.Interval, series.Source, series.Adjustment);
    }

    private void Fail(string message)
    {
        _lastError = message;
        if (Series is null)
        {
            Snapshot = null;
            PlotChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public static BarRequest BuildRequest(string symbol, ChartRangeKind range)
    {
        return range switch
        {
            ChartRangeKind.Session => new BarRequest(symbol, BarInterval.Minute1, SessionStart(), null),
            ChartRangeKind.Minute1 => new BarRequest(symbol, BarInterval.Minute1),
            ChartRangeKind.Minute5 => new BarRequest(symbol, BarInterval.Minute5),
            ChartRangeKind.Minute15 => new BarRequest(symbol, BarInterval.Minute15),
            ChartRangeKind.Hour1 => new BarRequest(symbol, BarInterval.Hour1),
            ChartRangeKind.Day1 => new BarRequest(symbol, BarInterval.Day1),
            ChartRangeKind.Week1 => new BarRequest(symbol, BarInterval.Week1),
            _ => new BarRequest(symbol, BarInterval.Month1),
        };
    }

    private static DateTimeOffset SessionStart()
    {
        var now = DateTimeOffset.Now;
        var et = MarketClock.ToEastern(now);
        var d = DateOnly.FromDateTime(et);
        if (et.TimeOfDay < new TimeSpan(4, 0, 0) || !MarketClock.IsTradingDay(d))
        {
            d = d.AddDays(-1);
            while (!MarketClock.IsTradingDay(d)) d = d.AddDays(-1);
        }
        return MarketClock.FromEastern(d.ToDateTime(new TimeOnly(4, 0)));
    }

    private void ArmTimer()
    {
        _timer.Interval = Range switch
        {
            ChartRangeKind.Session or ChartRangeKind.Minute1 or ChartRangeKind.Minute5 => TimeSpan.FromSeconds(15),
            ChartRangeKind.Minute15 or ChartRangeKind.Hour1 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(120),
        };
    }

    private void UpdateStatus()
    {
        if (_lastError is not null) { StatusText = _lastError; return; }
        if (Series is { } s)
        {
            var src = SourceNames.Display(s.Source, _loc);
            StatusText = string.Format(_loc["Ch_Source"], src, s.Bars.Count);
            if (s.OfflineCache)
                StatusText = string.Format(_loc["Ch_OfflineCache"], s.FetchedAt?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?") + "  ·  " + StatusText;
            var bad = s.Bars.Count(b => b.Incomplete || !b.IsConsistent());
            if (bad > 0) StatusText += "  ·  " + string.Format(_loc["Ch_IncompleteCount"], bad);
        }
        else StatusText = "";
    }

    public void UpdateCrosshair(int index)
    {
        if (Series is null || Snapshot is null || index < 0 || index >= Series.Bars.Count)
        {
            CrosshairText = "";
            return;
        }
        var inv = CultureInfo.InvariantCulture;
        var b = Series.Bars[index];
        var et = MarketClock.ToEastern(b.Time);
        var head = string.Format(_loc["Ch_Crosshair"],
            F(b.Open), F(b.High), F(b.Low), F(b.Close), _loc.Amount(b.Volume));
        var ma = Def(Snapshot.Sma[index]);
        var ema = Def(Snapshot.Ema[index]);
        CrosshairText = $"{et:yyyy-MM-dd HH:mm}   " + string.Format(_loc["Ch_CrosshairMore"], head, ma, ema)
            + (b.Incomplete || !b.IsConsistent() ? "   " + _loc["Ch_IncompleteBar"] : "");

        string F(double x) => NumberDisplay.Price(x);
        string Def(double x) => double.IsFinite(x) ? F(x) : "—";
    }
}