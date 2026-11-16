using Meili.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meili.Shared.Services;
using Meili.Core.Abstractions;
using Meili.Core.Export;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Core.Time;
using Meili.Data.Routing;

namespace Meili.Shared.ViewModels;

/// <summary>Watchlist page: grouped symbols, live quotes, search, add/remove/reorder.</summary>
public partial class WatchlistViewModel : ObservableObject
{
    private readonly WatchlistStore _store;
    private readonly IMarketDataService _data;
    private readonly LocalizationService _loc;
    private readonly NavigationHub _nav;
    private const string Owner = "watchlist";
    private readonly QuoteHub _hub;
    private readonly IUiTimer _searchDebounce = Ui.Timer(TimeSpan.FromMilliseconds(300));
    private readonly IUiTimer _flash = Ui.Timer(TimeSpan.FromMilliseconds(700));
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _searchCts;
    private DateTimeOffset? _lastUpdate;
    private string? _lastError;
    private string? _statusHint;
    private int _searchSeq;
    private bool _active;

    public ObservableCollection<WatchlistGroupTab> Groups { get; } = [];
    public ObservableCollection<WatchlistRowViewModel> Rows { get; } = [];
    public ObservableCollection<SymbolMatch> Suggestions { get; } = [];

    public bool HasSelectedRow => SelectedRow is not null;
    [ObservableProperty] public partial string SelectedGroupId { get; set; } = WatchlistGroup.DefaultId;
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string GroupNameDraft { get; set; } = "";
    [ObservableProperty] public partial bool IsAddingGroup { get; set; }
    [ObservableProperty] public partial bool IsRenamingGroup { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool ShowSuggestions { get; set; }
    [ObservableProperty] public partial bool ShowSearchEmpty { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedRow))] public partial WatchlistRowViewModel? SelectedRow { get; set; }
    [ObservableProperty] public partial bool HasRows { get; set; }

    public bool CanRemoveGroup => Groups.Count > 1;
    public string SelectedGroupTitle => Groups.FirstOrDefault(g => g.Id == SelectedGroupId)?.Title ?? _loc["Wl_DefaultGroup"];

    public WatchlistViewModel(WatchlistStore store, IMarketDataService data, QuoteHub hub, LocalizationService loc, NavigationHub nav)
    {
        _store = store; _data = data; _hub = hub; _loc = loc; _nav = nav;
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); await SearchAsync(); };
        _flash.Tick += (_, _) =>
        {
            _flash.Stop();
            foreach (var r in Rows) r.IsFlashing = false;
        };
        _store.Changed += (_, _) => Ui.Platform.Run(OnStoreChanged);
        Ui.Platform.ColorsChanged += (_, _) => { foreach (var r in Rows) r.Relabel(_loc); }; // E4: theme tones
        _loc.LanguageChanged += (_, _) =>
        {
            RebuildGroups();
            foreach (var r in Rows) r.Relabel(_loc);
            UpdateStatus();
            OnPropertyChanged(nameof(SelectedGroupTitle));
        };
        RebuildGroups();
        RebuildRows();
    }

    /// <summary>C1: listen to the shared quote hub while visible; the app-level pump does the periodic fetch.</summary>
    public async Task ActivateAsync()
    {
        if (!_active)
        {
            _active = true;
            _hub.Updated += OnBatch;
        }
        Resubscribe();
        await RefreshAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _hub.Updated -= OnBatch;
        _hub.Unsubscribe(Owner);
        _searchDebounce.Stop();_flash.Stop();foreach(var row in Rows)row.IsFlashing=false;
        _refreshCts?.Cancel();
        _searchCts?.Cancel();
    }

    private void OnStoreChanged()
    {
        RebuildGroups();
        RebuildRows();
        if (_active) _ = RefreshAsync();
    }

    private void RebuildGroups()
    {
        var keep = SelectedGroupId;
        Groups.Clear();
        foreach (var g in _store.Groups)
        {
            var title = string.IsNullOrEmpty(g.Name) ? _loc["Wl_DefaultGroup"] : g.Name;
            Groups.Add(new WatchlistGroupTab(g.Id, title, g.Symbols.Count, g.Id.Equals(keep, StringComparison.OrdinalIgnoreCase)));
        }
        if (Groups.All(g => !g.IsSelected))
        {
            var fallback = Groups.FirstOrDefault();
            if (fallback is not null)
            {
                fallback.IsSelected = true;
                SelectedGroupId = fallback.Id;
            }
        }
        OnPropertyChanged(nameof(CanRemoveGroup));
        OnPropertyChanged(nameof(SelectedGroupTitle));
    }

    private void RebuildRows()
    {
        var symbols = _store.FindGroup(SelectedGroupId)?.Symbols ?? (IReadOnlyList<string>)[];
        var map = Rows.ToDictionary(r => r.Symbol, StringComparer.OrdinalIgnoreCase);
        Rows.Clear();
        foreach (var s in symbols)
        {
            if (!map.TryGetValue(s, out var row)) row = new WatchlistRowViewModel(s);
            Rows.Add(row);
        }
        HasRows = Rows.Count > 0;
        Resubscribe();
        if (SelectedRow is not null && Rows.All(r => r.Symbol != SelectedRow.Symbol))
            SelectedRow = null;
    }

    partial void OnSelectedGroupIdChanged(string value)
    {
        foreach (var g in Groups) g.IsSelected = g.Id.Equals(value, StringComparison.OrdinalIgnoreCase);
        RebuildRows();
        OnPropertyChanged(nameof(SelectedGroupTitle));
        if (_active) _ = RefreshAsync();
    }

    partial void OnQueryChanged(string value)
    {
        _searchDebounce.Stop();
        ShowSearchEmpty = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            Suggestions.Clear();
            ShowSuggestions = false;
            return;
        }
        _searchDebounce.Start();
    }

    [RelayCommand]
    private void SelectGroup(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) SelectedGroupId = id;
    }

    private void Resubscribe()
    {
        if (_active) _hub.Subscribe(Owner, Rows.Select(r => r.Symbol));
    }

    private void OnBatch(object? sender, QuoteBatch batch)
    {
        if (!Rows.Any(r => batch.Covers(r.Symbol))) return;
        if (batch.Error is not null) { Failed(batch.Error); return; }
        Apply(batch.Quotes, batch.Requested);
        UpdateStatus();
    }

    private void Apply(IReadOnlyList<Quote> quotes, IReadOnlyList<string> requested)
    {
        var map = quotes.GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var flashing = false;
        foreach (var row in Rows)
            if (map.TryGetValue(row.Symbol, out var q) && row.Update(q, _loc)) flashing = true;
        if (flashing)
        {
            _flash.Stop();
            _flash.Start();
        }
        var missing = Rows.Where(r => requested.Contains(r.Symbol, StringComparer.OrdinalIgnoreCase) && !map.ContainsKey(r.Symbol))
            .Select(r => r.Symbol).ToList();
        _lastUpdate = DateTimeOffset.Now;
        if (map.Count > 0) AllSourcesFailed = false;
        _lastError = missing.Count > 0 ? string.Format(_loc["Wl_Missing"], string.Join(", ", missing)) : null;
    }

    /// <summary>D8: every source failed on the last attempt — show the InfoBar.</summary>
    [ObservableProperty] public partial bool AllSourcesFailed { get; set; }

    private void Failed(Exception ex)
    {
        _lastError = ErrorText(ex);
        AllSourcesFailed = ex is AllSourcesFailedException;
        var now = DateTimeOffset.Now;
        foreach (var r in Rows) r.RefreshStale(_loc, now);
        UpdateStatus();
    }

    [RelayCommand]
    private void OpenDataSources() => _nav.Go(AppPage.DataSources);

    private string ErrorText(Exception ex) => ex switch
    {
        AllSourcesFailedException => _loc["Wl_AllFailed"],
        OperationCanceledException => _loc["Wl_Timeout"],
        _ => ex.Message,
    };

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy || Rows.Count == 0)
        {
            if (Rows.Count == 0) { _lastError = null; UpdateStatus(); }
            return;
        }
        IsBusy = true;
        if (_lastUpdate is null) StatusText = _loc["Wl_Refreshing"];
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var symbols = Rows.Select(r => r.Symbol).ToList();
            var quotes = await _hub.GetQuotesAsync(symbols, _refreshCts.Token);
            Apply(quotes, symbols);
        }
        catch (OperationCanceledException) when (!_active) { }
        catch (Exception ex) { AppLog.Warn("watchlist", "refresh failed", ex); Failed(ex); }
        finally
        {
            _statusHint = null;
            IsBusy = false;
            UpdateStatus();
        }
    }

    private async Task SearchAsync()
    {
        var q = Query.Trim();
        if (q.Length == 0) return;
        var seq = ++_searchSeq;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var hits = await _data.SearchAsync(q, 8, _searchCts.Token);
            if (seq != _searchSeq) return;
            Suggestions.Clear();
            foreach (var h in hits) Suggestions.Add(h);
            ShowSearchEmpty = Suggestions.Count == 0;
            ShowSuggestions = Suggestions.Count > 0 || ShowSearchEmpty;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn("watchlist", $"search '{q}' failed", ex);
            if (seq != _searchSeq) return;
            Suggestions.Clear();
            ShowSearchEmpty = true;
            ShowSuggestions = true;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        await AddSymbolAsync(Query);
    }

    [RelayCommand]
    private async Task AddMatchAsync(SymbolMatch? match)
    {
        if (match is null) return;
        await AddSymbolAsync(match.Symbol);
    }

    public async Task AddSymbolAsync(string? raw)
    {
        HideSuggestions();
        var err = _store.AddSymbol(SelectedGroupId, raw ?? "", out var symbol);
        if (err != WatchlistError.None)
        {
            _statusHint = _loc[$"Wl_Err_{err}"];
            UpdateStatus();
            return;
        }
        Query = "";
        var others = _store.GroupsContaining(symbol).Where(g => g.Id != SelectedGroupId).Select(g => string.IsNullOrEmpty(g.Name) ? _loc["Wl_DefaultGroup"] : g.Name).ToList();
        _statusHint = others.Count == 0
            ? string.Format(_loc["Wl_Added"], symbol)
            : string.Format(_loc["Wl_AddedAlsoIn"], symbol, string.Join("、", others));
        UpdateStatus();
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedRow is null) return;
        var symbol = SelectedRow.Symbol;
        if (_store.RemoveSymbol(SelectedGroupId, symbol))
        {
            _statusHint = string.Format(_loc["Wl_Removed"], symbol);
            UpdateStatus();
        }
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedRow is not null) _store.MoveSymbol(SelectedGroupId, SelectedRow.Symbol, -1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedRow is not null) _store.MoveSymbol(SelectedGroupId, SelectedRow.Symbol, 1);
    }

    public void ReorderRows(string fromSymbol, string toSymbol)
    {
        var symbols = _store.FindGroup(SelectedGroupId)?.Symbols;
        if (symbols is null) return;
        var from = symbols.FindIndex(s => s.Equals(fromSymbol, StringComparison.OrdinalIgnoreCase));
        var to = symbols.FindIndex(s => s.Equals(toSymbol, StringComparison.OrdinalIgnoreCase));
        if (from >= 0 && to >= 0) _store.Reorder(SelectedGroupId, from, to);
    }

    [RelayCommand]
    private void BeginAddGroup()
    {
        IsRenamingGroup = false;
        IsAddingGroup = true;
        GroupNameDraft = "";
    }

    [RelayCommand]
    private void BeginRenameGroup()
    {
        IsAddingGroup = false;
        IsRenamingGroup = true;
        var g = _store.FindGroup(SelectedGroupId);
        GroupNameDraft = g is null || string.IsNullOrEmpty(g.Name) ? "" : g.Name;
    }

    [RelayCommand]
    private void CancelGroupEdit()
    {
        IsAddingGroup = false;
        IsRenamingGroup = false;
        GroupNameDraft = "";
    }

    [RelayCommand]
    private void ConfirmGroupEdit()
    {
        WatchlistError err;
        if (IsAddingGroup)
        {
            err = _store.AddGroup(GroupNameDraft, out var created);
            if (err == WatchlistError.None && created is not null)
                SelectedGroupId = created.Id;
        }
        else
        {
            err = _store.RenameGroup(SelectedGroupId, GroupNameDraft);
        }
        if (err != WatchlistError.None)
        {
            _statusHint = _loc[$"Wl_Err_{err}"];
            UpdateStatus();
            return;
        }
        CancelGroupEdit();
    }

    [RelayCommand]
    private async Task RemoveGroupAsync()
    {
        if (Groups.Count <= 1) return;
        var current = Groups.FirstOrDefault(g => g.IsSelected);
        var fallback = Groups.FirstOrDefault(g => !g.IsSelected);
        if (current is null || fallback is null) return;
        var msg = string.Format(_loc["Wl_ConfirmRemoveGroup"], current.Title, fallback.Title);
        if (!await Ui.Platform.ConfirmAsync(_loc["App_Title"], msg))
            return;
        var err = _store.RemoveGroup(current.Id);
        if (err != WatchlistError.None)
        {
            _statusHint = _loc[$"Wl_Err_{err}"];
            UpdateStatus();
            return;
        }
        SelectedGroupId = fallback.Id;
    }

    // D1: open the selected symbol on another page.
    [RelayCommand] private void OpenChart() => OpenOn(AppPage.Charts);
    [RelayCommand] private void OpenOptions() => OpenOn(AppPage.Options);
    [RelayCommand] private void OpenStrategy() => OpenOn(AppPage.Strategy);
    [RelayCommand] private void OpenNews() => OpenOn(AppPage.News);

    private void OpenOn(AppPage page)
    {
        if (SelectedRow?.Symbol is { } symbol) _nav.Go(page, symbol);
    }

    [RelayCommand]
    private void SetAlert()
    {
        var symbol = SelectedRow?.Symbol ?? Rows.FirstOrDefault()?.Symbol;
        _nav.Go(AppPage.Alerts, symbol);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var quotes = Rows.Select(r => r.Quote).OfType<Quote>().ToList();
        if (quotes.Count == 0)
        {
            SetHint("Ex_NoData");
            return;
        }
        var headers = new[]
        {
            _loc["Wl_ColSymbol"], _loc["Wl_ColName"], _loc["Wl_ColPrice"], _loc["Wl_ColChange"],
            _loc["Wl_ColChangePct"], _loc["Wl_ColVolume"], _loc["Wl_LastUpdated"], _loc["Wl_ColSource"],
            _loc["Op_Bid"], _loc["Op_Ask"], _loc["Wl_ColExt"], _loc["Wl_ColChangePct"], _loc["Ex_Session"],
        };
        var table = MarketExport.Quotes(SelectedGroupTitle, headers, quotes) with { Metadata = ExportUi.Metadata(_loc, null, note: _loc["Ex_MetaWatchNote"]) };
        var msg = await ExportUi.SaveAsync($"watchlist-{SelectedGroupTitle}", [table], _loc);
        if (msg is not null) { _statusHint = msg; UpdateStatus(); }
    }

    public void HideSuggestions()
    {
        ShowSuggestions = false;
        ShowSearchEmpty = false;
        Suggestions.Clear();
        _searchDebounce.Stop();
    }

    private void SetHint(string key)
    {
        _statusHint = _loc[key];
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_statusHint is not null)
        {
            StatusText = _statusHint;
            return;
        }
        if (_lastError is not null) { StatusText = _lastError; return; }
        StatusText = _lastUpdate is { } t
            ? $"{_loc["Wl_LastUpdated"]} {t:HH:mm:ss}  ·  {string.Format(_loc["Wl_Count"], Rows.Count)}"
            : string.Format(_loc["Wl_Count"], Rows.Count);
    }
}

public partial class WatchlistGroupTab(string id, string title, int count, bool selected) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] public partial string Title { get; set; } = title;
    [ObservableProperty] public partial int Count { get; set; } = count;
    [ObservableProperty] public partial bool IsSelected { get; set; } = selected;
}

public partial class WatchlistRowViewModel(string symbol) : ObservableObject
{
    private Quote? _quote;
    private double? _lastPrice;
    public string Symbol { get; } = symbol;
    public Quote? Quote => _quote;
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string PriceText { get; set; } = "—";
    [ObservableProperty] public partial string ChangeText { get; set; } = "—";
    [ObservableProperty] public partial string ChangePctText { get; set; } = "—";
    [ObservableProperty] public partial string ExtText { get; set; } = "";
    [ObservableProperty] public partial string VolumeText { get; set; } = "—";
    [ObservableProperty] public partial string SourceText { get; set; } = "";
    [ObservableProperty] public partial string TimingTip { get; set; } = "";
    [ObservableProperty] public partial object? ChangeBrush { get; set; }
    [ObservableProperty] public partial object? ChangeBackground { get; set; }
    [ObservableProperty] public partial bool IsFlashing { get; set; }
    [ObservableProperty] public partial bool IsStale { get; set; }
    [ObservableProperty] public partial string StaleTip { get; set; } = "";
    [ObservableProperty] public partial string PriceTimeText { get; set; } = "";

    /// <summary>D8: re-check the stale marker (also after a failed refresh).</summary>
    public void RefreshStale(LocalizationService loc, DateTimeOffset now)
    {
        if (_quote is not { } q) { IsStale = false; return; }
        var tip = FreshnessText.Stale(q, loc, now);
        IsStale = tip is not null;
        StaleTip = tip ?? "";
    }

    /// <returns>True when the last price changed and a flash should play.</returns>
    public bool Update(Quote q, LocalizationService loc)
    {
        var changed = _lastPrice is { } p && Math.Abs(p - q.Price) > 0.0001;
        _quote = q;
        _lastPrice = q.Price;
        Relabel(loc);
        if (changed) IsFlashing = true;
        return changed;
    }

    public void Relabel(LocalizationService loc)
    {
        if (_quote is not { } q) return;
        var inv = CultureInfo.InvariantCulture;
        Name = q.Name ?? "";
        PriceText = NumberDisplay.Price(q.Price);
        var (chg,pct)=QuoteMovement.Values(q);
        ChangeText = chg is { } c?NumberDisplay.SignedChange(c,q.Price):"—";
        ChangePctText = QuoteMovement.Percent(pct);
        var movement=chg??pct;
        var up=movement is >=0;
        ChangeBrush = Ui.Platform.Resource(movement is null?"UnknownBrush":up?"UpBrush":"DownBrush");
        ChangeBackground = Ui.Platform.Resource(movement is null?"ControlFillColorSecondaryBrush":up?"UpBackgroundBrush":"DownBackgroundBrush");
        ExtText = q.ExtendedPrice is { } ep
            ? $"{loc[q.MarketState == MarketState.PreMarket ? "Quote_Pre" : "Quote_Post"]}  {NumberDisplay.Price(ep)}  {QuoteMovement.Percent(q.ExtendedChangePercent)}"
            : "";
        VolumeText = q.Volume is >= 0 ? loc.Amount(q.Volume) : "—";
        var now = DateTimeOffset.Now;
        SourceText = FreshnessText.SourceAndTiming(q, loc, now);
        TimingTip = FreshnessText.Tooltip(q, loc, now);
        PriceTimeText = FreshnessText.PriceTime(q);
        RefreshStale(loc, now);
    }

}