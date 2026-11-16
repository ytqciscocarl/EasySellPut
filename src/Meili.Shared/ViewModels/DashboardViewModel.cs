using Meili.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meili.Shared.Services;
using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Core.Time;
using Meili.Data.Routing;

namespace Meili.Shared.ViewModels;

/// <summary>Overview page: live quote cards for the watchlist, refreshed adaptively by market session.</summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly WatchlistStore _watchlist;
    private const string Owner = "dashboard";
    private readonly QuoteHub _hub;
    private readonly LocalizationService _loc;
    private readonly NavigationHub _nav;
    private readonly INetworkController _network;
    private readonly SettingsStore _settings;
    private bool _active;
    private CancellationTokenSource? _cts;
    private DateTimeOffset? _lastUpdate;
    private string? _lastError;

    public ObservableCollection<QuoteCardViewModel> Quotes { get; } = [];
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    /// <summary>D8: every source failed on the last attempt — show the InfoBar.</summary>
    [ObservableProperty] public partial bool AllSourcesFailed { get; set; }
    /// <summary>D7: all sources failed and no local proxy was found — the InfoBar explains the proxy and offers re-detection.</summary>
    [ObservableProperty] public partial bool ProxyMissing { get; set; }
    [ObservableProperty] public partial string FailureBody { get; set; } = "";
    /// <summary>D7: getting-started card (network, watchlist, options) until the user closes it.</summary>
    [ObservableProperty] public partial bool ShowGuide { get; set; }
    [ObservableProperty] public partial string GuideNetworkText { get; set; } = "";

    public DashboardViewModel(WatchlistStore watchlist, QuoteHub hub, LocalizationService loc, NavigationHub nav,
        INetworkController network, SettingsStore settings)
    {
        _watchlist = watchlist; _hub = hub; _loc = loc; _nav = nav; _network = network; _settings = settings;
        ShowGuide = !settings.Current.GuideDismissed;
        DescribeNetwork();
        RebuildCards();
        _loc.LanguageChanged += (_, _) => { foreach (var q in Quotes) q.Relabel(_loc); UpdateStatus(); DescribeNetwork(); };
        Ui.Platform.ColorsChanged += (_, _) => { foreach (var q in Quotes) q.Relabel(_loc); }; // E4: theme tones
        _watchlist.Changed += (_, _) => Ui.Platform.Run(() => { RebuildCards(); if (_active) { Resubscribe(); _ = RefreshAsync(); } });
    }

    private void RebuildCards()
    {
        var wanted = _watchlist.AllSymbols.ToList();
        var map = Quotes.ToDictionary(q => q.Symbol, StringComparer.OrdinalIgnoreCase);
        Quotes.Clear();
        foreach (var s in wanted)
        {
            if (!map.TryGetValue(s, out var card)) card = new QuoteCardViewModel(s);
            Quotes.Add(card);
        }
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
        ShowCached();
        await RefreshAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _hub.Updated -= OnBatch;
        _hub.Unsubscribe(Owner);
        _cts?.Cancel();
    }

    private void Resubscribe() => _hub.Subscribe(Owner, Quotes.Select(q => q.Symbol));

    private void ShowCached()
    {
        foreach (var card in Quotes)
            if (_hub.TryGetCached(card.Symbol, out var q)) card.Update(q, _loc);
    }

    private void OnBatch(object? sender, QuoteBatch batch)
    {
        if (!Quotes.Any(c => batch.Covers(c.Symbol))) return;
        if (batch.Error is not null) { Failed(batch.Error); return; }
        Apply(batch.Quotes);
    }

    private void Failed(Exception ex)
    {
        _lastError = ErrorText(ex);
        AllSourcesFailed = ex is AllSourcesFailedException;
        DescribeNetwork();
        var now = DateTimeOffset.Now;
        foreach (var c in Quotes) c.RefreshStale(_loc, now);
        UpdateStatus();
    }

    [RelayCommand] private void OpenChart(string? symbol){if(!string.IsNullOrWhiteSpace(symbol))_nav.Go(AppPage.Charts,symbol);}

    [RelayCommand]
    private void OpenDataSources() => _nav.Go(AppPage.DataSources);

    [RelayCommand] private void OpenWatchlist() => _nav.Go(AppPage.Watchlist);
    [RelayCommand] private void OpenOptions() => _nav.Go(AppPage.Options);
    [RelayCommand] private void OpenSettings() => _nav.Go(AppPage.Settings);

    [RelayCommand]
    private void DismissGuide()
    {
        ShowGuide = false;
        _settings.Update(s => s.GuideDismissed = true);
    }

    /// <summary>D7: re-run proxy detection (e.g. after starting Clash) and fetch again.</summary>
    [RelayCommand]
    private async Task RedetectProxyAsync()
    {
        await _network.ReconfigureAsync();
        DescribeNetwork();
        await RefreshAsync();
    }

    private void DescribeNetwork()
    {
        var none = _network.NoProxyDetected;
        GuideNetworkText = none ? _loc["Dash_GuideNetNone"] : string.Format(_loc["Dash_GuideNetOk"], _network.EffectiveDescription);
        ProxyMissing = AllSourcesFailed && none;
        FailureBody = _loc[ProxyMissing ? "Dash_NoProxyBody" : "Dash_AllFailedBody"];
    }

    private void Apply(IReadOnlyList<Quote> quotes)
    {
        var map = quotes.GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var card in Quotes)
            if (map.TryGetValue(card.Symbol, out var q)) card.Update(q, _loc);
        if (map.Count > 0) { _lastUpdate = DateTimeOffset.Now; _lastError = null; AllSourcesFailed = false; DescribeNetwork(); }
        UpdateStatus();
    }

    private string ErrorText(Exception ex) => ex switch
    {
        AllSourcesFailedException => _loc["Dash_AllFailed"],
        OperationCanceledException => _loc["Dash_Timeout"],
        _ => ex.Message,
    };
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy || Quotes.Count == 0) return;
        IsBusy = true;
        if (_lastUpdate is null) StatusText = _loc["Dash_Loading"];
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            // Served from the hub's short-TTL cache when another page just fetched; otherwise one combined request.
            var quotes = await _hub.GetQuotesAsync(Quotes.Select(q => q.Symbol).ToList(), _cts.Token);
            Apply(quotes);
        }
        catch (OperationCanceledException) when (!_active) { }
        catch (Exception ex) { AppLog.Warn("dashboard", "refresh failed", ex); Failed(ex); }
        finally
        {
            IsBusy = false;
            UpdateStatus();
        }
    }

    private void UpdateStatus() => StatusText = _lastError is not null
        ? _lastError
        : _lastUpdate is { } t ? $"{_loc["Dash_LastUpdated"]} {t:HH:mm:ss}" : "";
}

public partial class QuoteCardViewModel(string symbol) : ObservableObject
{
    private Quote? _quote;
    public string Symbol { get; } = symbol;
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string PriceText { get; set; } = "—";
    [ObservableProperty] public partial string ChangeText { get; set; } = "";
    [ObservableProperty] public partial string ExtText { get; set; } = "";
    [ObservableProperty] public partial string SourceText { get; set; } = "";
    [ObservableProperty] public partial string AsOfText { get; set; } = "";
    [ObservableProperty] public partial string TimingTip { get; set; } = "";
    [ObservableProperty] public partial object? ChangeBrush { get; set; }
    [ObservableProperty] public partial object? ChangeBackground { get; set; }
    [ObservableProperty] public partial bool IsStale { get; set; }
    [ObservableProperty] public partial string StaleTip { get; set; } = "";
    [ObservableProperty] public partial string PriceTimeText { get; set; } = "";

    public void Update(Quote q, LocalizationService loc) { _quote = q; Relabel(loc); }

    /// <summary>D8: re-check the stale marker (also called when a refresh fails, so old prices get flagged).</summary>
    public void RefreshStale(LocalizationService loc, DateTimeOffset now)
    {
        if (_quote is not { } q) { IsStale = false; return; }
        var tip = FreshnessText.Stale(q, loc, now);
        IsStale = tip is not null;
        StaleTip = tip ?? "";
    }

    public string ChartActionLabel => string.Format(LocalizationService.Current["UX_OpenChartFor"],Symbol);
    public void Relabel(LocalizationService loc)
    {
        OnPropertyChanged(nameof(ChartActionLabel));
        if (_quote is not { } q) return;
        var inv = CultureInfo.InvariantCulture;
        Name = q.Name ?? "";
        PriceText = NumberDisplay.Price(q.Price);
        var (chg,pct)=QuoteMovement.Values(q);
        ChangeText = $"{(chg is { } c?NumberDisplay.SignedChange(c,q.Price):"—")}  {QuoteMovement.Percent(pct)}";
        var movement=chg??pct;
        var up=movement is >=0;
        ChangeBrush = Ui.Platform.Resource(movement is null?"UnknownBrush":up?"UpBrush":"DownBrush");
        ChangeBackground = Ui.Platform.Resource(movement is null?"ControlFillColorSecondaryBrush":up?"UpBackgroundBrush":"DownBackgroundBrush");
        ExtText = q.ExtendedPrice is { } ep
            ? $"{loc[q.MarketState == MarketState.PreMarket ? "Quote_Pre" : "Quote_Post"]}  {NumberDisplay.Price(ep)}  {QuoteMovement.Percent(q.ExtendedChangePercent)}"
            : "";
        var now = DateTimeOffset.Now;
        SourceText = FreshnessText.SourceAndTiming(q, loc, now);
        AsOfText = FreshnessText.AsOf(q.AsOf, loc);
        TimingTip = FreshnessText.Tooltip(q, loc, now);
        PriceTimeText = FreshnessText.PriceTime(q);
        RefreshStale(loc, now);
    }
}

public static class SourceNames
{
    /// <summary>Short localized source name (resource key <c>Src_{id}</c>).</summary>
    public static string Display(string id, LocalizationService loc) => loc.Get($"Src_{id}") is var s && s != $"Src_{id}" ? s : id;
}