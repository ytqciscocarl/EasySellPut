using Meili.Core.Abstractions;
using Meili.Core.Diagnostics;
using Meili.Core.Models;

namespace Meili.Shared.Services;

/// <summary>
/// H1/H2: the "best effort" side fetches shared by the options, strategy and news pages (spot quote, dividend yield).
/// Failures fall back to an empty result as before, but are now logged instead of swallowed.
/// </summary>
public static class SafeData
{
    public static async Task<IReadOnlyList<Quote>> QuotesAsync(IMarketDataService data, string symbol, string area, CancellationToken ct)
    {
        try { return await data.GetQuotesAsync([symbol], ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn(area, $"quote for {symbol} unavailable, continuing without it", ex);
            return [];
        }
    }

    /// <summary>Dividend yield as a fraction; 0 (with a log line) when the source has none or fails.</summary>
    public static async Task<double> DividendYieldAsync(IMarketDataService data, string symbol, string area, CancellationToken ct)
        => (await DividendObservationAsync(data, symbol, area, ct)).Yield;

    public static async Task<DividendYieldObservation> DividendObservationAsync(IMarketDataService data, string symbol, string area, CancellationToken ct)
    {
        try
        {
            var f = await data.GetFundamentalsAsync(symbol, ct);
            var known = string.Equals(f.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && f.DividendYield is >= 0 and < 1;
            return new(known ? f.DividendYield!.Value : 0, known, f.Source, f.AsOf, f.FetchedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLog.Warn(area, $"dividend yield for {symbol} unavailable, assuming 0", ex); return new(0, false, null, null, null); }
    }
}
public sealed record DividendYieldObservation(double Yield, bool Reported, string? Source, DateTimeOffset? AsOf, DateTimeOffset? FetchedAt);
