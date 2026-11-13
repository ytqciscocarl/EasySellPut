using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Data.Cache;

namespace Meili.Data.Tests;

/// <summary>B2: SQLite K-line cache with tail-only refresh, re-adjustment detection and offline fallback.</summary>
public sealed class BarCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bar-cache-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset D0 = new(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Inner : IMarketDataService
    {
        public List<BarRequest> Requests { get; } = [];
        public Func<BarRequest, BarSeries>? Make { get; set; }
        public bool Down { get; set; }
        public int Days { get; set; } = 40;
        public double Scale { get; set; } = 1;
        public string Source { get; set; } = "yahoo";
        public PriceAdjustment Basis { get; set; } = PriceAdjustment.Split;

        public Task<BarSeries> GetBarsAsync(BarRequest r, CancellationToken ct = default)
        {
            Requests.Add(r);
            if (Down) throw new HttpRequestException("offline");
            var bars = Enumerable.Range(0, Days).Select(d => Bar.FromSource(D0.AddDays(d), 100 + d, 101 + d, 99 + d, (100.5 + d) * Scale, 1000))
                .Where(b => r.From is null || b.Time >= r.From).ToList();
            return Task.FromResult(new BarSeries { Symbol = r.Symbol, Interval = r.Interval, Bars = bars, Source = Source, FetchedAt = D0.AddDays(Days), Adjustment = Basis });
        }
        public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Quote>>([]);
        public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OptionChain> GetChainAsync(string symbol, DateOnly? expiration = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Fundamentals> GetFundamentalsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FinancialStatements> GetFinancialsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, int count = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private (CachedMarketData Svc, Inner Inner) Make()
    {
        var inner = new Inner();
        return (new CachedMarketData(inner, new MarketDb(Path.Combine(_dir, "market.db"))), inner);
    }

    [Fact]
    public async Task Round_trips_bars_including_missing_fields()
    {
        var db = new MarketDb(Path.Combine(_dir, "rt.db"));
        Assert.Equal(MarketDb.SchemaVersion, db.ReadSchemaVersion());
        var bars = new[] { Bar.FromSource(D0, 1, 2, 0.5, 1.5, 10), Bar.CloseOnly(D0.AddMinutes(1), 1.6, 3) };
        db.SaveBars("AAPL", BarInterval.Minute1, bars, new BarCacheMeta("cboe", D0, D0, 15, false));
        var (back, meta) = db.LoadBars("AAPL", BarInterval.Minute1);
        Assert.Equal(2, back.Count);
        Assert.Equal(bars[0], back[0]);
        Assert.True(double.IsNaN(back[1].Open) && back[1].Incomplete);
        Assert.Equal(("cboe", 15), (meta!.Source, meta.DelayMinutes));
        Assert.Empty(db.LoadBars("AAPL", BarInterval.Day1).Bars);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Second_request_downloads_only_a_tail_and_merges()
    {
        var (svc, inner) = Make();
        var first = await svc.GetBarsAsync(new BarRequest("aapl", BarInterval.Day1));
        Assert.Equal(40, first.Bars.Count);
        inner.Days = 42;                                     // two new sessions since
        var second = await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        Assert.Equal(42, second.Bars.Count);
        Assert.Equal(2, inner.Requests.Count);
        Assert.NotNull(inner.Requests[1].From);              // tail only
        Assert.True(inner.Requests[1].From > D0.AddDays(30));
        Assert.Equal((1, 1), (svc.FullFetches, svc.TailFetches));
        Assert.True(second.Bars.Zip(second.Bars.Skip(1)).All(p => p.First.Time < p.Second.Time));
    }

    [Fact]
    public async Task Readjusted_history_or_new_source_forces_a_full_reload()
    {
        var (svc, inner) = Make();
        await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        inner.Scale = 0.5;                                    // split: every close halves
        var after = await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        Assert.Equal(2, svc.FullFetches);
        Assert.Equal(40, after.Bars.Count);
        Assert.Equal(100.5 * 0.5, after.Bars[0].Close, 6);    // old cached history replaced

        inner.Source = "cboe";
        await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        Assert.Equal(3, svc.FullFetches);
    }

    [Fact]
    public async Task Offline_serves_the_cache_and_says_so()
    {
        var (svc, inner) = Make();
        await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        inner.Down = true;
        var s = await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        Assert.True(s.OfflineCache);
        Assert.Equal(40, s.Bars.Count);
        Assert.Equal(D0.AddDays(40), s.FetchedAt);
        Assert.False(s.IsRealtime);
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.GetBarsAsync(new BarRequest("MSFT", BarInterval.Day1)));
    }

    [Fact]
    public async Task Narrow_first_request_does_not_serve_a_wider_one()
    {
        var (svc, inner) = Make();
        await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1, D0.AddDays(20)));   // e.g. "today's session"
        await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));                  // default window
        Assert.Equal(2, svc.FullFetches);
        Assert.Null(inner.Requests[1].From);
        var narrow = await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1, D0.AddDays(30)));
        Assert.Equal(1, svc.TailFetches);                                                    // default window covers it
        Assert.All(narrow.Bars, b => Assert.True(b.Time >= D0.AddDays(30)));
    }

    // ---- F7: adjustment basis in the cache ----

    [Fact]
    public async Task Modes_are_cached_separately_and_basis_round_trips()
    {
        var (svc, inner) = Make();
        await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        inner.Basis = PriceAdjustment.SplitDividend;
        var total = await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1, Adjust: AdjustMode.Total));
        Assert.Equal(2, svc.FullFetches);                                 // separate cache entry, not a tail of the split one
        Assert.Equal(PriceAdjustment.SplitDividend, total.Adjustment);
        var (_, meta) = svc.Db.LoadBars("AAPL", BarInterval.Day1, "total");
        Assert.Equal(PriceAdjustment.SplitDividend, meta!.Basis);
        Assert.Equal(PriceAdjustment.Split, svc.Db.LoadBars("AAPL", BarInterval.Day1).Meta!.Basis);

        inner.Down = true;                                                 // offline view keeps the basis
        var offline = await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1, Adjust: AdjustMode.Total));
        Assert.True(offline.OfflineCache);
        Assert.Equal(PriceAdjustment.SplitDividend, offline.Adjustment);
    }

    [Fact]
    public async Task A_basis_change_forces_a_full_reload_instead_of_splicing()
    {
        var (svc, inner) = Make();
        await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        inner.Basis = PriceAdjustment.SplitDividend;                      // same source, different basis
        var s = await svc.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1));
        Assert.Equal((2, 1), (svc.FullFetches, svc.TailFetches));
        Assert.Equal(PriceAdjustment.SplitDividend, s.Adjustment);
    }

    [Fact]
    public void A_version_1_database_is_upgraded_in_place()
    {
        var path = Path.Combine(_dir, "v1.db");
        Directory.CreateDirectory(_dir);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE schema_version (v INTEGER NOT NULL); INSERT INTO schema_version VALUES (1);
                CREATE TABLE bars (symbol TEXT NOT NULL, interval TEXT NOT NULL, adjust TEXT NOT NULL DEFAULT '',
                    t INTEGER NOT NULL, o REAL, h REAL, l REAL, c REAL NOT NULL, v REAL NOT NULL, incomplete INTEGER NOT NULL,
                    PRIMARY KEY (symbol, interval, adjust, t)) WITHOUT ROWID;
                CREATE TABLE bar_meta (symbol TEXT NOT NULL, interval TEXT NOT NULL, adjust TEXT NOT NULL DEFAULT '',
                    source TEXT NOT NULL, fetched_at INTEGER NOT NULL, as_of INTEGER, delay_minutes INTEGER, realtime INTEGER NOT NULL, covered_from INTEGER,
                    PRIMARY KEY (symbol, interval, adjust));
                INSERT INTO bar_meta VALUES ('AAPL','Day1','','yahoo',1,NULL,NULL,0,NULL);
                INSERT INTO bars VALUES ('AAPL','Day1','',1,1,1,1,1,1,0);
                """;
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var db = new MarketDb(path);
        Assert.Equal(MarketDb.SchemaVersion, db.ReadSchemaVersion());
        var (bars, meta) = db.LoadBars("AAPL", BarInterval.Day1);
        Assert.Single(bars);
        Assert.Equal(PriceAdjustment.Unknown, meta!.Basis);               // refreshed by the next fetch
    }

    // ---- Brief C: SQLite work off the caller's context, and only changed rows are written ----

    private static Bar B(int i, double? close = null)
    {
        var c = close ?? 100.5 + i;
        return Bar.FromSource(D0.AddMinutes(i), 100 + i, Math.Max(101 + i, c), 99 + i, c, 1000);
    }

    private static List<Bar> Series(int from, int to) => Enumerable.Range(from, to - from).Select(i => B(i)).ToList();

    private static readonly BarRequest Req = new("AAPL", BarInterval.Minute1);

    /// <summary>Serves a scripted list of bars (honours <see cref="BarRequest.From"/> unless <see cref="Respond"/> is set).</summary>
    private sealed class Scripted : IMarketDataService
    {
        public List<Bar> Bars { get; set; } = [];
        public Func<BarRequest, IReadOnlyList<Bar>>? Respond { get; set; }
        public DateTimeOffset FetchedAt { get; set; } = D0;
        public bool Yield { get; set; }
        /// <summary>True when any call arrived while a <see cref="SynchronizationContext"/> was current (i.e. on the caller's context).</summary>
        public bool CalledOnContext { get; private set; }

        public async Task<BarSeries> GetBarsAsync(BarRequest r, CancellationToken ct = default)
        {
            if (SynchronizationContext.Current is not null) CalledOnContext = true;
            if (Yield) await Task.Yield();
            var bars = Respond?.Invoke(r) ?? Bars.Where(b => r.From is null || b.Time >= r.From).ToList();
            return new BarSeries { Symbol = r.Symbol, Interval = r.Interval, Bars = bars, Source = "yahoo", FetchedAt = FetchedAt, Adjustment = PriceAdjustment.Split };
        }
        public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OptionChain> GetChainAsync(string symbol, DateOnly? expiration = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Fundamentals> GetFundamentalsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FinancialStatements> GetFinancialsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, int count = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Stands in for the UI context: counts every continuation that is marshalled back to it.</summary>
    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                try { d(state); }
                finally { SetSynchronizationContext(null); }
            });
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            d(state);
        }
    }

    private (CachedMarketData Svc, Scripted Inner) MakeScripted()
    {
        var inner = new Scripted();
        return (new CachedMarketData(inner, new MarketDb(Path.Combine(_dir, "market.db"))), inner);
    }

    private static IReadOnlyList<Bar> Rows(CachedMarketData svc) => svc.Db.LoadBars("AAPL", BarInterval.Minute1).Bars;

    /// <summary>Starts the call with the counting context current (like a UI-thread caller), then awaits without capturing it.</summary>
    private static async Task<(BarSeries Series, int Posts)> CallOnCountingContext(CachedMarketData svc)
    {
        var ctx = new CountingContext();
        var previous = SynchronizationContext.Current;
        Task<BarSeries> pending;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try { pending = svc.GetBarsAsync(Req); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var series = await pending.ConfigureAwait(false);
        return (series, ctx.Posts);
    }

    [Fact]
    public async Task Cache_work_never_touches_the_callers_context()
    {
        var (svc, inner) = MakeScripted();
        inner.Yield = true;                                   // a network await in the middle, like the real source
        inner.Bars = Series(0, 40);
        var (first, firstPosts) = await CallOnCountingContext(svc);   // full load
        Assert.Equal(40, first.Bars.Count);
        Assert.Equal(0, firstPosts);

        inner.Bars = Series(0, 41);
        inner.FetchedAt = D0.AddHours(1);
        var (second, secondPosts) = await CallOnCountingContext(svc); // tail refresh
        Assert.Equal(41, second.Bars.Count);
        Assert.Equal(0, secondPosts);
        Assert.Equal((1, 1), (svc.FullFetches, svc.TailFetches));
        Assert.False(inner.CalledOnContext);                  // the source is not called on the caller's context either
    }

    [Fact]
    public async Task An_unchanged_refresh_writes_no_bar_rows_but_refreshes_the_metadata()
    {
        var (svc, inner) = MakeScripted();
        inner.Bars = Series(0, 40);
        await svc.GetBarsAsync(Req);
        Assert.Equal(40, svc.BarRowsWritten);
        Assert.Equal(D0, svc.Db.LoadBars("AAPL", BarInterval.Minute1).Meta!.FetchedAt);

        inner.FetchedAt = D0.AddHours(2);
        var again = await svc.GetBarsAsync(Req);
        Assert.Equal(1, svc.TailFetches);
        Assert.Equal(40, svc.BarRowsWritten);                 // nothing rewritten
        var (rows, meta) = svc.Db.LoadBars("AAPL", BarInterval.Minute1);
        Assert.Equal(D0.AddHours(2), meta!.FetchedAt);        // but the refresh time moved on
        Assert.Equal(again.Bars, rows);
        Assert.Equal(Series(0, 40), rows);
    }

    [Fact]
    public async Task One_new_bar_writes_only_the_few_rows_that_changed()
    {
        var (svc, inner) = MakeScripted();
        inner.Bars = Series(0, 40);
        await svc.GetBarsAsync(Req);
        var before = svc.BarRowsWritten;

        inner.Bars = Series(0, 41);
        inner.FetchedAt = D0.AddHours(1);
        var s = await svc.GetBarsAsync(Req);
        Assert.InRange(svc.BarRowsWritten - before, 1, CachedMarketData.OverlapBars + 1);
        Assert.Equal(41, s.Bars.Count);
        Assert.Equal(s.Bars, Rows(svc));
        Assert.Equal(Series(0, 41), Rows(svc));
    }

    [Fact]
    public async Task An_in_progress_last_bar_is_replaced_in_place()
    {
        var (svc, inner) = MakeScripted();
        inner.Bars = Series(0, 40);
        await svc.GetBarsAsync(Req);
        var before = svc.BarRowsWritten;

        var closed = Bar.FromSource(D0.AddMinutes(39), 139, 150, 130, 145, 2000);   // the last minute finished differently
        inner.Bars = [.. Series(0, 39), closed];
        inner.FetchedAt = D0.AddHours(1);
        var s = await svc.GetBarsAsync(Req);
        Assert.Equal(1, svc.BarRowsWritten - before);
        Assert.Equal(closed, s.Bars[^1]);
        Assert.Equal(40, s.Bars.Count);
        Assert.Equal(s.Bars, Rows(svc));
    }

    [Fact]
    public async Task A_bar_missing_from_the_tail_is_removed_from_the_store_too()
    {
        var (svc, inner) = MakeScripted();
        inner.Bars = Series(0, 40);
        await svc.GetBarsAsync(Req);

        inner.Bars = Series(0, 40).Where(b => b.Time != D0.AddMinutes(36)).ToList();    // the source dropped bar 36
        var s = await svc.GetBarsAsync(Req);
        Assert.Equal(1, svc.TailFetches);
        Assert.Equal(39, s.Bars.Count);
        Assert.DoesNotContain(s.Bars, b => b.Time == D0.AddMinutes(36));
        Assert.Equal(s.Bars, Rows(svc));                      // no stale row left behind
    }

    [Fact]
    public async Task Growing_past_max_bars_trims_the_oldest_rows()
    {
        var (svc, inner) = MakeScripted();
        inner.Bars = Series(0, CachedMarketData.MaxBars);
        await svc.GetBarsAsync(Req);
        Assert.Equal(CachedMarketData.MaxBars, svc.BarRowsWritten);

        inner.Bars = Series(0, CachedMarketData.MaxBars + 3);
        var s = await svc.GetBarsAsync(Req);
        Assert.Equal(1, svc.TailFetches);
        Assert.Equal(CachedMarketData.MaxBars, s.Bars.Count);
        Assert.Equal(D0.AddMinutes(3), s.Bars[0].Time);
        Assert.Equal(CachedMarketData.MaxBars + 3, svc.BarRowsWritten);   // only the 3 new bars were written
        Assert.Equal(s.Bars, Rows(svc));
    }

    [Fact]
    public async Task An_oversized_full_load_is_trimmed_by_the_next_unchanged_refresh()
    {
        var (svc, inner) = MakeScripted();
        inner.Bars = Series(0, CachedMarketData.MaxBars + 3);             // full loads are stored untrimmed
        await svc.GetBarsAsync(Req);
        Assert.Equal(CachedMarketData.MaxBars + 3, Rows(svc).Count);

        var s = await svc.GetBarsAsync(Req);
        Assert.Equal(1, svc.TailFetches);
        Assert.Equal(CachedMarketData.MaxBars, s.Bars.Count);
        Assert.Equal(CachedMarketData.MaxBars + 3, svc.BarRowsWritten);   // trimming alone writes no bars
        Assert.Equal(s.Bars, Rows(svc));
    }

    [Fact]
    public async Task A_gap_forces_a_full_reload_and_the_store_matches()
    {
        var (svc, inner) = MakeScripted();
        inner.Bars = Series(0, 40);
        await svc.GetBarsAsync(Req);

        inner.Respond = r => r.From is null ? Series(0, 70) : Series(60, 70);   // the tail starts after the last cached bar
        var s = await svc.GetBarsAsync(Req);
        Assert.Equal((2, 1), (svc.FullFetches, svc.TailFetches));
        Assert.Equal(70, s.Bars.Count);
        Assert.Equal(s.Bars, Rows(svc));
    }

    [Fact]
    public async Task Stored_rows_equal_the_served_series_after_merges_and_full_reloads()
    {
        var (svc, inner) = Make();
        var req = new BarRequest("AAPL", BarInterval.Day1);
        async Task Check()
        {
            var s = await svc.GetBarsAsync(req);
            Assert.Equal(s.Bars, svc.Db.LoadBars("AAPL", BarInterval.Day1).Bars);
        }
        await Check();                                         // first full load
        inner.Days = 42; await Check();                        // tail merge
        inner.Scale = 0.5; await Check();                      // re-adjusted history: full reload
        inner.Basis = PriceAdjustment.SplitDividend; await Check();   // basis change: full reload
        inner.Days = 43; await Check();                        // tail merge again on the reloaded series
        Assert.Equal(3, svc.FullFetches);
    }

    [Fact]
    public void ReplaceTail_deletes_a_range_inserts_and_updates_the_metadata_in_one_call()
    {
        var db = new MarketDb(Path.Combine(_dir, "rt2.db"));
        var meta = new BarCacheMeta("yahoo", D0, D0, null, false);
        db.SaveBars("AAPL", BarInterval.Minute1, Series(0, 10), meta);
        db.SaveBars("MSFT", BarInterval.Minute1, Series(0, 10), meta);

        var later = meta with { FetchedAt = D0.AddHours(1) };
        db.ReplaceTail("AAPL", BarInterval.Minute1, "", [B(8, 150), B(9), B(10)], D0.AddMinutes(8), D0.AddMinutes(2), later);

        var (rows, m) = db.LoadBars("AAPL", BarInterval.Minute1);
        Assert.Equal([.. Enumerable.Range(2, 6).Select(i => B(i)), B(8, 150), B(9), B(10)], rows);   // 0,1 trimmed; 8 replaced; 10 new
        Assert.Equal(D0.AddHours(1), m!.FetchedAt);
        Assert.Equal(Series(0, 10), db.LoadBars("MSFT", BarInterval.Minute1).Bars);                   // other series untouched
        Assert.Equal(D0, db.LoadBars("MSFT", BarInterval.Minute1).Meta!.FetchedAt);

        db.ReplaceTail("AAPL", BarInterval.Minute1, "", [], null, null, meta with { FetchedAt = D0.AddHours(2) });
        var (same, m2) = db.LoadBars("AAPL", BarInterval.Minute1);
        Assert.Equal(rows, same);                              // no bars, no deletes: metadata only
        Assert.Equal(D0.AddHours(2), m2!.FetchedAt);
    }
}

/// <summary>B6: open interest per contract and day, compared with the previous stored day.</summary>
public class OpenInterestHistoryTests
{
    [Fact]
    public void Previous_day_is_the_latest_stored_day_before()
    {
        var dir = Directory.CreateTempSubdirectory("oi-").FullName;
        try
        {
            var db = new MarketDb(Path.Combine(dir, "m.db"));
            var d1 = new DateOnly(2026, 11, 9);
            var d2 = new DateOnly(2026, 11, 11);
            db.SaveOpenInterest("AAPL", d1, [("C1", 100), ("C2", 50)]);
            db.SaveOpenInterest("AAPL", d2, [("C1", 130)]);
            db.SaveOpenInterest("AAPL", d2, [("C1", 140)]);                    // same day: replaced
            db.SaveOpenInterest("MSFT", d1, [("C1", 999)]);
            Assert.Empty(db.LoadPreviousOpenInterest("AAPL", d1));
            Assert.Equal(new Dictionary<string, double> { ["C1"] = 100, ["C2"] = 50 }, db.LoadPreviousOpenInterest("AAPL", d2));
            Assert.Equal(new Dictionary<string, double> { ["C1"] = 140 }, db.LoadPreviousOpenInterest("AAPL", d2.AddDays(1)));
            Assert.Equal(3, db.PruneOpenInterest(d2));                             // every symbol's rows before d2
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
