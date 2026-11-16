using Meili.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meili.Shared.Services;
using Meili.Core.Abstractions;
using Meili.Core.Export;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Core.Time;
using Meili.Data.Routing;

namespace Meili.Shared.ViewModels;

/// <summary>News and fundamentals page: profile, key stats, financial statements, article list.</summary>
public partial class NewsViewModel : ObservableObject
{
    private static readonly TimeZoneInfo BeijingZone = ResolveBeijing();

    private readonly WatchlistStore _watchlist;
    private readonly IMarketDataService _data;
    private readonly LocalizationService _loc;
    private readonly SymbolContext _context;
    private readonly IUiTimer _timer = Ui.Timer(TimeSpan.FromMinutes(3));
    private CancellationTokenSource? _cts;
    private bool _active;
    private string? _lastError;
    private FinancialStatements? _financials;
    private Fundamentals? _lastFundamentals;

    public ObservableCollection<string> WatchSymbols { get; } = [];
    public ObservableCollection<StatItem> Stats { get; } = [];
    public ObservableCollection<NewsEntry> News { get; } = [];
    public StatementBlock Income { get; } = new();
    public StatementBlock Balance { get; } = new();
    public StatementBlock CashFlow { get; } = new();

    [ObservableProperty] public partial string SymbolText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool Annual { get; set; } = true;
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string Headline { get; set; } = "";
    [ObservableProperty] public partial string NameText { get; set; } = "";
    [ObservableProperty] public partial string PriceText { get; set; } = "";
    [ObservableProperty] public partial string ChangeText { get; set; } = "";
    [ObservableProperty] public partial object? ChangeBrush { get; set; }
    [ObservableProperty] public partial string MetaText { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string WebsiteText { get; set; } = "";
    [ObservableProperty] public partial string? WebsiteUrl { get; set; }
    [ObservableProperty] public partial string EmployeesText { get; set; } = "";
    [ObservableProperty] public partial bool HasProfile { get; set; }
    [ObservableProperty] public partial bool HasNews { get; set; }
    [ObservableProperty] public partial bool HasFinancials { get; set; }
    [ObservableProperty] public partial bool HasWebsite { get; set; }

    public NewsViewModel(WatchlistStore watchlist, IMarketDataService data, LocalizationService loc, SymbolContext context)
    {
        _watchlist = watchlist; _data = data; _loc = loc; _context = context;
        // D1: follow the shared symbol while visible; when hidden, ActivateAsync adopts it on the next visit.
        _context.Changed += (_, sym) => { if (_active && _context.ShouldAdopt(SymbolText)) _ = AdoptContextAsync(); };
        SymbolText = watchlist.AllSymbols.Contains("AAPL", StringComparer.OrdinalIgnoreCase)
            ? "AAPL"
            : (watchlist.AllSymbols.FirstOrDefault() ?? "AAPL");
        RebuildWatchSymbols();
        _timer.Tick += async (_, _) => await LoadAsync(refreshOnly: true);
        _watchlist.Changed += (_, _) => RebuildWatchSymbols();
        _loc.LanguageChanged += (_, _) => Relabel();
        Ui.Platform.ColorsChanged += (_, _) => ChangeBrush = UpDownBrush(_up); // E4: theme tones
    }

    public async Task ActivateAsync()
    {
        _active = true;
        if (_context.ShouldAdopt(SymbolText)) { await AdoptContextAsync(); return; }
        if (string.IsNullOrEmpty(Headline) && Stats.Count == 0 && News.Count == 0) await LoadAsync();
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

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refreshOnly: true);

    [RelayCommand]
    private async Task ExportAsync()
    {
        var tables = new List<ExportTable>();
        if (Stats.Count > 0)
            tables.Add(new ExportTable
            {
                Name = _loc["Nf_Stats"],
                Headers = [_loc["Ex_Field"], _loc["Ex_Value"]],
                Rows = Stats.Select(s => (IReadOnlyList<string>)[s.Label, s.Value]).ToList(),
            });
        foreach (var block in new[] { Income, Balance, CashFlow })
        {
            if (!block.HasData) continue;
            var headers = new List<string> { _loc["Nf_Item"] };
            foreach (var h in new[] { block.H0, block.H1, block.H2, block.H3 })
                if (!string.IsNullOrWhiteSpace(h)) headers.Add(h);
            tables.Add(new ExportTable
            {
                Name = string.IsNullOrWhiteSpace(block.Title) ? "statement" : block.Title,
                Headers = headers,
                Rows = block.Rows.Select(r => (IReadOnlyList<string>)new[] { r.Label, r.V0, r.V1, r.V2, r.V3 }.Take(headers.Count).ToArray()).ToList(),
            });
        }
        if (News.Count > 0)
            tables.Add(new ExportTable
            {
                Name = _loc["Nf_News"],
                Headers = [_loc["Ex_Headline"], _loc["Ex_Publisher"], _loc["Ex_Time"], _loc["Ex_Url"]],
                Rows = News.Select(n => (IReadOnlyList<string>)[n.Title, n.Publisher, n.TimeText, n.Url]).ToList(),
            });
        // F9: the profile/statistics come from the fundamentals source; statements carry their own (e.g. SEC).
        var meta = ExportUi.Metadata(_loc, (SourcedData?)_lastFundamentals ?? _financials);
        var msg = await ExportUi.SaveAsync(string.IsNullOrWhiteSpace(SymbolText) ? "fundamentals" : SymbolText + "-fundamentals", ExportUi.WithMetadata(tables, meta), _loc);
        if (msg is not null) StatusText = msg;
    }

    [RelayCommand]
    private void SelectAnnual(bool annual)
    {
        Annual = annual;
    }

    [RelayCommand]
    private void OpenArticle(NewsEntry? item)
    {
        if (item is null) return;
        if (!NewsReaderPolicy.IsAllowed(item.Url)) { StatusText = _loc["Reader_Blocked"]; return; }
        try { Ui.Platform.OpenNewsArticle(item.Url, item.Title); }
        catch (Exception ex) { AppLog.Warn("news", "Could not open news reader", ex); StatusText = _loc["Reader_Failed"]; }
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        if (!TryOpenUrl(WebsiteUrl)) StatusText = _loc["Nf_OpenFailed"];
    }

    partial void OnAnnualChanged(bool value) => RebuildFinancials();

    public async Task LoadAsync(bool refreshOnly = false)
    {
        if (!WatchlistStore.TryNormalizeSymbol(SymbolText, out var symbol, out _))
        {
            if (!refreshOnly)
            {
                _lastError = _loc["Nf_InvalidSymbol"];
                UpdateStatus();
            }
            return;
        }
        SymbolText = symbol;
        _context.Set(symbol);
        IsBusy = true;
        if (!refreshOnly) StatusText = _loc["Nf_Loading"];
        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = _cts.Token;
        try
        {
            var quoteTask = SafeQuotes(symbol, ct);
            var fundTask = SafeFundamentals(symbol, ct);
            var finTask = SafeFinancials(symbol, ct);
            var newsTask = SafeNews(symbol, ct);
            var quotes = await quoteTask;
            var (fund, fundErr) = await fundTask;
            var (fin, finErr) = await finTask;
            var (news, newsErr) = await newsTask;
            var quote = quotes.FirstOrDefault(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

            _lastFundamentals = fund;
            ApplyQuoteAndProfile(symbol, quote, fund);
            ApplyStats(quote, fund);
            _financials = fin;
            RebuildFinancials();
            ApplyNews(news);

            var bits = new List<string>();
            if (fund is not null) bits.Add(string.Format(_loc["Nf_SourceFund"], SourceNames.Display(fund.Source, _loc)));
            if (fin is not null) bits.Add(string.Format(_loc["Nf_SourceFin"], SourceNames.Display(fin.Source, _loc)));
            if (news is { Count: > 0 })
                bits.Add(string.Format(_loc["Nf_SourceNews"], SourceNames.Display(news[0].Source, _loc), news.Count));
            _lastError = bits.Count > 0 ? null : (fundErr ?? finErr ?? newsErr ?? _loc["Nf_NoData"]);
            if (bits.Count > 0)
            {
                StatusText = string.Join("   ·   ", bits);
                var notes = new List<string>();
                if (fund is null && fundErr is not null) notes.Add(_loc["Nf_FundFailed"]);
                if (fin is null && finErr is not null) notes.Add(_loc["Nf_FinFailed"]);
                if ((news is null || news.Count == 0) && newsErr is not null) notes.Add(_loc["Nf_NewsFailed"]);
                if (notes.Count > 0) StatusText += "   ·   " + string.Join("  ", notes);
            }
        }
        catch (OperationCanceledException) when (_active && !refreshOnly)
        {
            _lastError = _loc["Nf_Timeout"];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("news", "refresh failed", ex); _lastError = ex.Message; }
        finally
        {
            IsBusy = false;
            if (_active) _timer.Start();
            UpdateStatus();
        }
    }

    private void ApplyQuoteAndProfile(string symbol, Quote? quote, Fundamentals? fund)
    {
        var name = fund?.Name ?? quote?.Name ?? symbol;
        NameText = name;
        var price = quote is { Price: > 0 } ? quote.Price : (double?)null;
        PriceText = NumberDisplay.Price(price);
        var ch = quote?.Change;
        var pct = quote?.ChangePercent;
        if (ch is { } c && pct is { } p)
            ChangeText = $"{NumberDisplay.SignedChange(c, price ?? c)}  ({(p >= 0 ? "+" : "")}{p.ToString("0.00", CultureInfo.InvariantCulture)}%)";
        else
            ChangeText = "";
        _up = (ch ?? 0) >= 0;
        ChangeBrush = UpDownBrush(_up);
        Headline = string.IsNullOrEmpty(PriceText) || PriceText == NumberDisplay.Empty
            ? $"{name}  ({symbol})"
            : $"{name}  ({symbol})   {PriceText}";

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(fund?.Sector)) meta.Add(fund!.Sector!);
        if (!string.IsNullOrWhiteSpace(fund?.Industry) && fund!.Industry != fund.Sector) meta.Add(fund.Industry!);
        if (!string.IsNullOrWhiteSpace(fund?.Country)) meta.Add(fund!.Country!);
        if (!string.IsNullOrWhiteSpace(fund?.Exchange ?? quote?.Exchange)) meta.Add(fund?.Exchange ?? quote?.Exchange!);
        MetaText = string.Join("  ·  ", meta);
        Description = fund?.Description?.Trim() ?? "";
        WebsiteUrl = NormalizeUrl(fund?.Website);
        WebsiteText = WebsiteUrl is null ? "" : DisplayHost(WebsiteUrl);
        HasWebsite = WebsiteUrl is not null;
        EmployeesText = fund?.Employees is { } e && e > 0 ? NumberDisplay.Integer(e) : "";
        HasProfile = !string.IsNullOrWhiteSpace(Description) || !string.IsNullOrWhiteSpace(MetaText) || HasWebsite;
    }

    private void ApplyStats(Quote? quote, Fundamentals? fund)
    {
        Stats.Clear();
        void Add(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && value != NumberDisplay.Empty)
                Stats.Add(new StatItem(_loc[key], value));
        }

        var cap = fund?.MarketCap ?? quote?.MarketCap;
        var hi = fund?.FiftyTwoWeekHigh ?? quote?.FiftyTwoWeekHigh;
        var lo = fund?.FiftyTwoWeekLow ?? quote?.FiftyTwoWeekLow;
        Add("Nf_MarketCap", _loc.Amount(cap));
        Add("Nf_PE", NumberDisplay.Ratio(fund?.TrailingPE));
        Add("Nf_ForwardPE", NumberDisplay.Ratio(fund?.ForwardPE));
        Add("Nf_EPS", NumberDisplay.Price(fund?.Eps));
        var div = fund?.DividendRate;
        var yield = fund?.DividendYield;
        if (div is { } d && d > 0 && yield is { } y && y > 0)
            Add("Nf_Dividend", $"{NumberDisplay.Price(d)}  ({NumberDisplay.Percent(y <= 1 ? y : y / 100)})");
        else
        {
            Add("Nf_Dividend", NumberDisplay.Price(div));
            Add("Nf_Yield", NumberDisplay.Percent(yield is { } yy ? (yy <= 1 ? yy : yy / 100) : null));
        }
        Add("Nf_Range52", NumberDisplay.Range(lo, hi));
        Add("Nf_Beta", NumberDisplay.Ratio(fund?.Beta));
        Add("Nf_AvgVolume", _loc.Amount(fund?.AverageVolume ?? quote?.Volume));
        Add("Nf_Shares", _loc.Amount(fund?.SharesOutstanding));
        Add("Nf_PB", NumberDisplay.Ratio(fund?.PriceToBook));
        Add("Nf_ProfitMargin", NumberDisplay.Percent(fund?.ProfitMargin is { } pm ? (Math.Abs(pm) <= 1 ? pm : pm / 100) : null));
        Add("Nf_Revenue", _loc.Amount(fund?.Revenue));
        Add("Nf_Target", NumberDisplay.Price(fund?.TargetPrice));
        Add("Nf_Earnings", fund?.NextEarningsDate is { } ed ? ed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
        Add("Nf_ExDiv", fund?.ExDividendDate is { } xd ? xd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
        if (!string.IsNullOrWhiteSpace(fund?.QuoteType)) Add("Nf_Type", fund!.QuoteType!);
        if (!string.IsNullOrWhiteSpace(EmployeesText)) Add("Nf_Employees", EmployeesText);
    }

    private void RebuildFinancials()
    {
        var lines = _financials?.Lines ?? [];
        FillBlock(Income, StatementTable.Build(lines, FinancialStatementKind.Income, Annual), "Nf_Income");
        FillBlock(Balance, StatementTable.Build(lines, FinancialStatementKind.Balance, Annual), "Nf_Balance");
        FillBlock(CashFlow, StatementTable.Build(lines, FinancialStatementKind.CashFlow, Annual), "Nf_CashFlow");
        HasFinancials = Income.HasData || Balance.HasData || CashFlow.HasData;
    }

    private void FillBlock(StatementBlock block, StatementTable table, string titleKey)
    {
        block.Title = _loc[titleKey];
        block.HasData = !table.IsEmpty;
        block.H0 = Head(table, 0); block.H1 = Head(table, 1); block.H2 = Head(table, 2); block.H3 = Head(table, 3);
        block.Rows.Clear();
        foreach (var row in table.Rows)
        {
            block.Rows.Add(new StatementLine(
                ItemLabel(row.Item),
                Cell(row, 0, row.Item), Cell(row, 1, row.Item), Cell(row, 2, row.Item), Cell(row, 3, row.Item)));
        }
        block.Raise();
    }

    private static string Head(StatementTable table, int i)
        => i < table.Periods.Count ? table.Periods[i].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    private string Cell(StatementRow row, int i, string item)
    {
        if (i >= row.Values.Count || row.Values[i] is not { } v) return "";
        return item == FinancialItems.DilutedEps ? NumberDisplay.Price(v) : _loc.Amount(v);
    }

    private string ItemLabel(string item) => item switch
    {
        FinancialItems.TotalRevenue => _loc["Nf_TotalRevenue"],
        FinancialItems.GrossProfit => _loc["Nf_GrossProfit"],
        FinancialItems.OperatingIncome => _loc["Nf_OperatingIncome"],
        FinancialItems.NetIncome => _loc["Nf_NetIncome"],
        FinancialItems.DilutedEps => _loc["Nf_DilutedEPS"],
        FinancialItems.TotalAssets => _loc["Nf_TotalAssets"],
        FinancialItems.TotalLiabilities => _loc["Nf_TotalLiabilities"],
        FinancialItems.StockholdersEquity => _loc["Nf_StockholdersEquity"],
        FinancialItems.OperatingCashFlow => _loc["Nf_OperatingCashFlow"],
        FinancialItems.CapitalExpenditure => _loc["Nf_CapitalExpenditure"],
        FinancialItems.FreeCashFlow => _loc["Nf_FreeCashFlow"],
        _ => item,
    };

    private void ApplyNews(IReadOnlyList<NewsItem>? items)
    {
        News.Clear();
        if (items is null) { HasNews = false; return; }
        var now = DateTimeOffset.Now;
        foreach (var n in items)
        {
            if (string.IsNullOrWhiteSpace(n.Title) || !IsSafeUrl(n.Url)) continue;
            News.Add(new NewsEntry
            {
                Title = n.Title.Trim(),
                Url = n.Url,
                Publisher = string.IsNullOrWhiteSpace(n.Publisher) ? SourceNames.Display(n.Source, _loc) : n.Publisher.Trim(),
                TimeText = FormatRel(n.Published, now),
                TimeTip = FormatTip(n.Published),
                Summary = string.IsNullOrWhiteSpace(n.Summary) ? "" : n.Summary.Trim(),
            });
        }
        HasNews = News.Count > 0;
    }

    private string FormatRel(DateTimeOffset? published, DateTimeOffset now)
    {
        if (published is not { } t) return "";
        var d = now - t;
        if (d.TotalMinutes < 1) return _loc["Nf_JustNow"];
        if (d.TotalMinutes < 60) return string.Format(_loc["Nf_MinutesAgo"], Math.Max(1, (int)d.TotalMinutes));
        if (d.TotalHours < 24) return string.Format(_loc["Nf_HoursAgo"], Math.Max(1, (int)d.TotalHours));
        if (d.TotalDays < 7) return string.Format(_loc["Nf_DaysAgo"], Math.Max(1, (int)d.TotalDays));
        return TimeZoneInfo.ConvertTime(t, BeijingZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private string FormatTip(DateTimeOffset? published)
    {
        if (published is not { } t) return "";
        var et = TimeZoneInfo.ConvertTime(t, MarketClock.Eastern).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var bj = TimeZoneInfo.ConvertTime(t, BeijingZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return string.Format(_loc["Nf_TimeEtBj"], et, bj);
    }

    private bool _up = true;

    private static object? UpDownBrush(bool up) => Ui.Platform.Resource(up ? "UpBrush" : "DownBrush");

    private void Relabel()
    {
        // Rebuild labels from the last payload we still have in collections / financials.
        RebuildFinancials();
        if (!string.IsNullOrEmpty(SymbolText))
            _ = LoadAsync(refreshOnly: true);
    }

    private void UpdateStatus()
    {
        if (_lastError is not null) StatusText = _lastError;
    }

    private Task<IReadOnlyList<Quote>> SafeQuotes(string symbol, CancellationToken ct) => SafeData.QuotesAsync(_data, symbol, "news", ct);

    private async Task<(Fundamentals? Data, string? Error)> SafeFundamentals(string symbol, CancellationToken ct)
    {
        try { return (await _data.GetFundamentalsAsync(symbol, ct), null); }
        catch (AllSourcesFailedException) { return (null, _loc["Nf_FundFailed"]); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Warn("news", $"load for {symbol} failed", ex); return (null, ex.Message); }
    }

    private async Task<(FinancialStatements? Data, string? Error)> SafeFinancials(string symbol, CancellationToken ct)
    {
        try { return (await _data.GetFinancialsAsync(symbol, ct), null); }
        catch (AllSourcesFailedException) { return (null, _loc["Nf_FinFailed"]); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Warn("news", $"load for {symbol} failed", ex); return (null, ex.Message); }
    }

    private async Task<(IReadOnlyList<NewsItem>? Data, string? Error)> SafeNews(string symbol, CancellationToken ct)
    {
        try { return (await _data.GetNewsAsync(symbol, 30, ct), null); }
        catch (AllSourcesFailedException) { return (null, _loc["Nf_NewsFailed"]); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Warn("news", $"load for {symbol} failed", ex); return (null, ex.Message); }
    }

    public static bool TryOpenUrl(string? url)
    {
        if (!IsSafeUrl(url)) return false;
        try
        {
            Ui.Platform.OpenUrl(url!);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("news", $"could not open {url}", ex);
            return false;
        }
    }

    public static bool IsSafeUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string? NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        return IsSafeUrl(s) ? s : null;
    }

    private static string DisplayHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase) : url;

    private static TimeZoneInfo ResolveBeijing()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch // expected: Windows id missing on non-Windows / trimmed ICU — try the IANA id next
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
            catch { return TimeZoneInfo.Local; } // expected: last resort, local time is the user's own zone
        }
    }
}

public sealed record StatItem(string Label, string Value);

public sealed class NewsEntry
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string Publisher { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string TimeTip { get; init; } = "";
    public string Summary { get; init; } = "";
}

public sealed class StatementBlock : ObservableObject
{
    private string _title = "", _h0 = "", _h1 = "", _h2 = "", _h3 = "";
    private bool _hasData;
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string H0 { get => _h0; set => SetProperty(ref _h0, value); }
    public string H1 { get => _h1; set => SetProperty(ref _h1, value); }
    public string H2 { get => _h2; set => SetProperty(ref _h2, value); }
    public string H3 { get => _h3; set => SetProperty(ref _h3, value); }
    public bool HasData { get => _hasData; set => SetProperty(ref _hasData, value); }
    public ObservableCollection<StatementLine> Rows { get; } = [];
    public void Raise()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(H0));
        OnPropertyChanged(nameof(H1));
        OnPropertyChanged(nameof(H2));
        OnPropertyChanged(nameof(H3));
        OnPropertyChanged(nameof(HasData));
    }
}

public sealed record StatementLine(string Label, string V0, string V1, string V2, string V3);