using System.Collections.Concurrent;
using Meili.Core.Abstractions;
using Meili.Core.Diagnostics;
using Meili.Core.Models;

namespace Meili.Data.Cache;

/// <summary>
/// B2 + C2: wraps the failover router and keeps K-line history in <see cref="MarketDb"/>.
/// <list type="bullet">
/// <item>First request: full download, saved.</item>
/// <item>Later requests: only a short tail (a few bars of overlap) is downloaded and merged, so refreshes and restarts
/// are cheap and less likely to hit rate limits.</item>
/// <item>A different source, re-adjusted history (overlap disagrees) or a gap: full download replaces the cache.</item>
/// <item>Network down: the cached bars are returned with <see cref="BarSeries.OfflineCache"/> set.</item>
/// </list>
/// Requests with an explicit end (<see cref="BarRequest.To"/>) bypass the cache. Everything else passes through.
/// </summary>
public sealed class CachedMarketData : IMarketDataService
{
    public const int OverlapBars = 5;
    /// <summary>Tail merges append forever; keep the newest bars only (≈ 5 trading days of 1-minute bars with extended hours).</summary>
    public const int MaxBars = 6000;
    private readonly IMarketDataService _inner;
    private readonly MarketDb _db;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public CachedMarketData(IMarketDataService inner, MarketDb db)
    {
        _inner = inner;
        _db = db;
    }

    public MarketDb Db => _db;
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Counters for tests and diagnostics.</summary>
    public int FullFetches { get; private set; }
    public int TailFetches { get; private set; }
    /// <summary>Bar rows inserted into the store (full saves and tail updates), for tests: unchanged refreshes must not grow it.</summary>
    public int BarRowsWritten => Volatile.Read(ref _barRowsWritten);
    private int _barRowsWritten;

    public async Task<BarSeries> GetBarsAsync(BarRequest request, CancellationToken ct = default)
    {
        if (request.To is not null) return await _inner.GetBarsAsync(request, ct).ConfigureAwait(false);
        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var gate = _locks.GetOrAdd($"{symbol}|{request.Interval}|{request.Adjust}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The store reads, the merge and the writes are synchronous SQLite work: keep them off the caller's (UI) context.
            var core = request with { Symbol = symbol };
            return await Task.Run(() => GetCoreAsync(core, ct), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<BarSeries> GetCoreAsync(BarRequest request, CancellationToken ct)
    {
        (IReadOnlyList<Bar> Bars, BarCacheMeta? Meta) cached;
        try { cached = _db.LoadBars(request.Symbol, request.Interval, request.Adjust.CacheKey()); }
        catch (Exception ex)
        {
            AppLog.Warn("cache", $"read failed, bypassing: {ex.Message}");
            return await _inner.GetBarsAsync(request, ct).ConfigureAwait(false);
        }

        // The cache can serve this request only if it was filled from at least as far back as asked for.
        var usable = cached.Meta is { } meta && cached.Bars.Count > OverlapBars
            && (meta.CoveredFrom is null || (request.From is { } rf && rf >= meta.CoveredFrom));
        if (!usable) return await FullAsync(request, cached, ct).ConfigureAwait(false);

        BarSeries tail;
        try
        {
            TailFetches++;
            tail = await _inner.GetBarsAsync(request with { From = BarMerge.TailStart(cached.Bars, OverlapBars) }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("cache", $"{request.Symbol} {request.Interval}: network failed, serving cache ({ex.Message})");
            return Offline(request, cached.Bars, cached.Meta!);
        }

        if (!string.Equals(tail.Source, cached.Meta!.Source, StringComparison.OrdinalIgnoreCase))
            return await FullAsync(request, cached, ct, $"source changed {cached.Meta.Source}→{tail.Source}").ConfigureAwait(false);
        // F7: never splice bars of different adjustment bases (e.g. a pre-F7 cache, or a source that changed its basis).
        if (tail.Adjustment != cached.Meta.Basis)
            return await FullAsync(request, cached, ct, $"adjustment changed {cached.Meta.Basis}→{tail.Adjustment}").ConfigureAwait(false);
        var (full, outcome) = BarMerge.Merge(cached.Bars, tail.Bars);
        if (outcome is BarMergeOutcome.Diverged or BarMergeOutcome.Gap)
            return await FullAsync(request, cached, ct, outcome.ToString()).ConfigureAwait(false);

        var trim = Math.Max(0, full.Count - MaxBars);
        var merged = trim > 0 ? full.Skip(trim).ToList() : full;
        SaveMerged(request, cached.Bars, full, trim, MetaOf(tail, cached.Meta.CoveredFrom));
        return Shape(request, tail, merged);
    }

    private async Task<BarSeries> FullAsync(BarRequest request, (IReadOnlyList<Bar> Bars, BarCacheMeta? Meta) cached, CancellationToken ct, string? why = null)
    {
        if (why is not null) AppLog.Info("cache", $"{request.Symbol} {request.Interval}: full reload ({why})");
        BarSeries full;
        try
        {
            FullFetches++;
            full = await _inner.GetBarsAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch when (cached.Meta is not null && cached.Bars.Count > 0)
        {
            return Offline(request, cached.Bars, cached.Meta);
        }
        Save(request, full.Bars, MetaOf(full, request.From));
        return full;
    }

    private BarCacheMeta MetaOf(BarSeries from, DateTimeOffset? coveredFrom) =>
        new(from.Source ?? "", from.FetchedAt ?? Now(), from.AsOf, from.DelayMinutes, from.IsRealtime, coveredFrom, from.Adjustment);

    /// <summary>Full reload: replaces the whole stored series.</summary>
    private void Save(BarRequest request, IReadOnlyList<Bar> bars, BarCacheMeta meta)
    {
        try
        {
            _db.SaveBars(request.Symbol, request.Interval, bars, meta, request.Adjust.CacheKey());
            Interlocked.Add(ref _barRowsWritten, bars.Count);
        }
        catch (Exception ex) { AppLog.Warn("cache", $"write failed: {ex.Message}"); }
    }

    /// <summary>
    /// Tail merge: writes only what differs from the stored series. <paramref name="full"/> is the merged series before the
    /// <see cref="MaxBars"/> trim (<paramref name="trim"/> oldest bars are dropped); it equals <paramref name="cached"/> up to
    /// the first changed bar, so everything before that stays untouched. With no change only the metadata is refreshed.
    /// </summary>
    private void SaveMerged(BarRequest request, IReadOnlyList<Bar> cached, IReadOnlyList<Bar> full, int trim, BarCacheMeta meta)
    {
        var i = 0;
        while (i < full.Count && i < cached.Count && full[i] == cached[i]) i++;

        // Rows from the first difference onward are deleted and rewritten. Take the earlier of the two times so a cached bar
        // that the merged series dropped (a hole in the new tail) cannot survive.
        DateTimeOffset? deleteFrom = null;
        if (i < full.Count && i < cached.Count) deleteFrom = full[i].Time < cached[i].Time ? full[i].Time : cached[i].Time;
        else if (i < full.Count) deleteFrom = full[i].Time;
        else if (i < cached.Count) deleteFrom = cached[i].Time;

        var start = Math.Max(i, trim);
        IReadOnlyList<Bar> bars = start < full.Count ? full.Skip(start).ToList() : [];
        DateTimeOffset? deleteBefore = trim > 0 ? full[trim].Time : null;
        try
        {
            _db.ReplaceTail(request.Symbol, request.Interval, request.Adjust.CacheKey(), bars, deleteFrom, deleteBefore, meta);
            Interlocked.Add(ref _barRowsWritten, bars.Count);
        }
        catch (Exception ex) { AppLog.Warn("cache", $"write failed: {ex.Message}"); }
    }

    private static BarSeries Shape(BarRequest request, BarSeries meta, IReadOnlyList<Bar> bars)
    {
        var list = request.From is { } f ? bars.Where(b => b.Time >= f).ToList() : bars;
        return meta with { Symbol = request.Symbol, Interval = request.Interval, Bars = list };
    }

    private static BarSeries Offline(BarRequest request, IReadOnlyList<Bar> bars, BarCacheMeta meta) =>
        new()
        {
            Symbol = request.Symbol, Interval = request.Interval,
            Bars = request.From is { } f ? bars.Where(b => b.Time >= f).ToList() : bars,
            Source = meta.Source, AsOf = meta.AsOf, FetchedAt = meta.FetchedAt, DelayMinutes = meta.DelayMinutes,
            IsRealtime = false, OfflineCache = true, Adjustment = meta.Basis,
        };

    // ---------------- pass-through ----------------
    public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default) => _inner.GetQuotesAsync(symbols, ct);
    public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string symbol, CancellationToken ct = default) => _inner.GetExpirationsAsync(symbol, ct);
    public Task<OptionChain> GetChainAsync(string symbol, DateOnly? expiration = null, CancellationToken ct = default) => _inner.GetChainAsync(symbol, expiration, ct);
    public Task<Fundamentals> GetFundamentalsAsync(string symbol, CancellationToken ct = default) => _inner.GetFundamentalsAsync(symbol, ct);
    public Task<FinancialStatements> GetFinancialsAsync(string symbol, CancellationToken ct = default) => _inner.GetFinancialsAsync(symbol, ct);
    public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, int count = 20, CancellationToken ct = default) => _inner.GetNewsAsync(symbol, count, ct);
    public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 10, CancellationToken ct = default) => _inner.SearchAsync(query, limit, ct);
}
