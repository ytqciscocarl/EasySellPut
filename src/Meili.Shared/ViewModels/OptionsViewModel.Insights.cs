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

/// <summary>B6 (split out in H1): IV Rank, ATM term structure and OI change for the options page.</summary>
public partial class OptionsViewModel
{
    // B6: IV Rank (from the locally recorded IV history), ATM term structure, OI change vs. the previous stored day.
    [ObservableProperty] public partial string IvRankText { get; set; } = "";
    [ObservableProperty] public partial string IvRankTip { get; set; } = "";
    [ObservableProperty] public partial bool ShowTerm { get; set; }
    [ObservableProperty] public partial bool ShowOiChange { get; set; } = true;
    public IReadOnlyList<TermPoint> TermPoints { get; private set; } = [];

    /// <summary>B6: stores today's open interest and returns the previous stored day's (empty when there is none).</summary>
    private async Task<IReadOnlyDictionary<string, double>> OpenInterestBeforeAsync(string symbol, OptionChain chain)
    {
        var day = DateOnly.FromDateTime(MarketClock.ToEastern(chain.AsOf ?? DateTimeOffset.Now).Date);
        try { return await _insights.SwapOpenInterestAsync(symbol, day, chain.Contracts); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            AppLog.Warn("options", "open interest history unavailable", ex);
            return new Dictionary<string, double>();
        }
    }

    private (Meili.Core.Alerts.IvSample Sample, IvRankResult? Rank, int Days)? _ivRank;

    /// <summary>IV rank tag and tip in the current language (also re-run when the language changes).</summary>
    private void RelabelIvRank()
    {
        if (_ivRank is not var (sample, rank, days))
        {
            IvRankText = "";
            IvRankTip = "";
            return;
        }
        var inv = CultureInfo.InvariantCulture;
        if (rank is { } r)
        {
            IvRankText = string.Format(inv, _loc["Op_IvRank"], r.Rank, r.Percentile);
            IvRankTip = string.Format(inv, _loc["Op_IvRankTip"], r.Days, AlertText.IvBasisText(sample, _loc), r.Low * 100, r.High * 100, sample.Iv * 100);
        }
        else
        {
            IvRankText = string.Format(inv, _loc["Op_IvRankPending"], days, Meili.Analytics.IvRank.MinDays);
            IvRankTip = _loc["Op_IvRankPendingTip"];
        }
    }

    private async Task LoadInsightsAsync(string symbol, IReadOnlyList<DateOnly> expirations, double spot, CancellationToken ct)
    {
        _ivRank = null;
        RelabelIvRank();
        TermPoints = [];
        try
        {
            var now = DateTimeOffset.Now;
            _ivRank = await _insights.RankAsync(symbol, now, ct);
            RelabelIvRank();
            TermPoints = await _insights.TermStructureAsync(symbol, expirations, spot, now, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is AllSourcesFailedException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Warn("options", "volatility insights failed", ex);
        }
        if (string.Equals(symbol, _loadedSymbol, StringComparison.OrdinalIgnoreCase)) TableChanged?.Invoke(this, EventArgs.Empty);
    }
}
