using System.Globalization;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Avalonia.Threading;
using Meili.Core.Diagnostics;

namespace Meili.Mac.Platform;

public sealed record MenuBarQuote(string Title, string Tooltip, int Direction, IReadOnlyList<string> Details)
{
    public static MenuBarQuote Create(string symbol, Quote? quote, bool failed, LocalizationService loc, DateTimeOffset now)
    {
        if (quote is null || !string.Equals(symbol, quote.Symbol, StringComparison.OrdinalIgnoreCase) || !double.IsFinite(quote.Price) || quote.Price <= 0)
            return new(symbol + " —", loc["Mb_Unavailable"], 0, [loc["Mb_Unavailable"]]);
        // Regular-session price is explicit. Extended-session quotes stay separately labelled in the menu.
        double? change = quote.ChangePercent is { } p && double.IsFinite(p) ? p
            : quote.PreviousClose is > 0 and var previous && double.IsFinite(previous) ? (quote.Price / previous - 1) * 100 : null;
        if (change is { } value && !double.IsFinite(value)) change = null;
        var stale = failed || QuoteStaleness.Check(quote, now) != StaleReason.None;
        var freshness = DataFreshness.Evaluate(quote, now);
        var timing = stale ? loc["Mb_Stale"] : freshness.Kind switch
        {
            FreshnessKind.Delayed => $"{freshness.Lag!.Value.TotalMinutes:0}m",
            FreshnessKind.Measured when freshness.LagMinutes >= 1 => $"~{freshness.LagMinutes:0}m",
            FreshnessKind.Unknown => "?",
            _ => ""
        };
        var movement = change is { } c ? $" {(c > 0 ? "↑" : c < 0 ? "↓" : "↔")}{Math.Abs(c).ToString(Math.Abs(c) > 99999 ? "0.##E+0" : "0.00", CultureInfo.InvariantCulture)}%" : "";
        var title = symbol + " " + (quote.Price >= 1e9 ? quote.Price.ToString("0.##E+0", CultureInfo.InvariantCulture) : NumberDisplay.Price(quote.Price)) + movement + (timing.Length > 0 ? " · " + timing : "");
        var details = new List<string> { loc["Mb_Regular"] + " · " + quote.Currency, FreshnessText.SourceAndTiming(quote, loc, now), FreshnessText.AsOf(quote.AsOf, loc) };
        if (quote.FetchedAt is not null) details.Add(FreshnessText.Fetched(quote.FetchedAt, loc));
        if (stale) details.Add(loc["Mb_Stale"]);
        if (quote.MarketState is MarketState.PreMarket or MarketState.AfterHours && quote.ExtendedPrice is > 0 and var ep && double.IsFinite(ep))
            details.Add(loc[quote.MarketState == MarketState.PreMarket ? "Quote_Pre" : "Quote_Post"] + " " + NumberDisplay.Price(ep) + " · " + FreshnessText.AsOf(quote.ExtendedTime, loc));
        return new(title, string.Join("\n", new[] { title }.Concat(details)), stale ? 0 : change > 0 ? 1 : change < 0 ? -1 : 0, details);
    }
}

public sealed record QuoteMenuEntry(string Title, Action? Action = null, bool Checked = false, bool Separator = false);
public interface IQuoteStatusItem : IDisposable
{
    void Update(MenuBarQuote quote, IReadOnlyList<QuoteMenuEntry> entries, UpDownColorMode colors);
}

/// <summary>One native presentation subscriber; the existing QuotePump owns all periodic fetching.</summary>
public sealed class MenuBarQuoteController : IDisposable
{
    private readonly QuoteHub _hub;
    private readonly SymbolContext _context;
    private readonly WatchlistStore _watchlist;
    private readonly LocalizationService _loc;
    private readonly SettingsStore _settings;
    private readonly IQuoteStatusItem _item;
    private readonly Action _open, _quit;
    private readonly Action? _hide;
    private readonly CancellationTokenSource _lifetime = new();
    private string _symbol;
    private Quote? _quote;
    private bool _failed, _disposed;
    private readonly string _owner = "mac-menu-bar-" + Guid.NewGuid().ToString("N");
    public MenuBarQuoteController(QuoteHub hub, SymbolContext context, WatchlistStore watchlist, LocalizationService loc, SettingsStore settings, IQuoteStatusItem item, Action open, Action quit, Action? hide = null)
    {
        _hub = hub; _context = context; _watchlist = watchlist; _loc = loc; _settings = settings; _item = item; _open = open; _quit = quit; _hide = hide;
        _symbol = context.Current ?? watchlist.AllSymbols.FirstOrDefault() ?? "SPY";
        hub.Updated += OnQuotes; context.Changed += OnSymbol; watchlist.Changed += OnLabels; loc.LanguageChanged += OnLabels; MacTheme.ColorsChanged += OnLabels;
        Select(_symbol);
    }
    private void OnSymbol(object? sender, string symbol) => Select(symbol);
    private void OnLabels(object? sender, EventArgs e)
    {
        // Settings applies the theme before committing its preference; read the final state next UI turn.
        Dispatcher.UIThread.Post(Render);
    }
    private void Select(string symbol)
    {
        if (_disposed) return;
        _symbol = symbol; _failed = false;
        _quote = _hub.TryGetCached(symbol, out var cached) ? cached : null;
        _hub.Subscribe(_owner, [symbol]); Render();
        _ = RefreshAsync(symbol);
    }
    private async Task RefreshAsync(string symbol)
    {
        try { await _hub.GetQuotesAsync([symbol], _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Warn("menu-bar", "Quote refresh failed", ex); }
    }
    private void OnQuotes(object? sender, QuoteBatch batch)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnQuotes(sender, batch)); return; }
        if (_disposed || !batch.Covers(_symbol)) return;
        var current = batch.Find(_symbol);
        _failed = batch.Error is not null || current is null;
        if (current is not null) _quote = current;
        Render();
    }
    private void Render()
    {
        if (_disposed) return;
        var presentation = MenuBarQuote.Create(_symbol, _quote, _failed, _loc, DateTimeOffset.UtcNow);
        var entries = presentation.Details.Select(d => new QuoteMenuEntry(d)).ToList();
        entries.Add(new("", Separator: true));
        entries.Add(new(_loc["Mb_OpenChart"], () => { _context.Set(_symbol); _open(); }));
        entries.Add(new(_loc["Mb_Refresh"], () => _ = RefreshAsync(_symbol)));
        if (_hide is not null) entries.Add(new(_loc["Mb_Hide"], _hide));
        entries.Add(new("", Separator: true));
        entries.Add(new(_loc["Mb_Follows"]));
        foreach (var symbol in _watchlist.AllSymbols.Prepend(_symbol).Distinct(StringComparer.OrdinalIgnoreCase).Take(100))
        { var chosen = symbol; entries.Add(new(symbol, () => _context.Set(chosen), symbol == _symbol)); }
        entries.Add(new("", Separator: true)); entries.Add(new(_loc["Menu_Quit"], _quit));
        _item.Update(presentation, entries, _settings.Current.EffectiveUpDownColors(_loc.Language));
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _lifetime.Cancel(); _hub.Unsubscribe(_owner); _hub.Updated -= OnQuotes; _context.Changed -= OnSymbol;
        _watchlist.Changed -= OnLabels; _loc.LanguageChanged -= OnLabels; MacTheme.ColorsChanged -= OnLabels;
        _item.Dispose(); _lifetime.Dispose();
    }
}
