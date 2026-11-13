using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Data.Http;
using Meili.Data.Routing;

namespace Meili.Data.Tests;

public class RouterTests
{
    private sealed class FakeQuotes(string id, Func<IReadOnlyList<string>, IReadOnlyList<Quote>> f) : IQuoteProvider, IBarsProvider
    {
        public int Calls;
        public List<string[]> Requests { get; } = [];
        public string Id => id;
        public string DisplayName => id;
        public DataCapability Capabilities => DataCapability.Quote | DataCapability.DailyBars;
        public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
        { Calls++; Requests.Add(symbols.ToArray()); return Task.FromResult(f(symbols)); }
        public bool Supports(BarInterval i) => i == BarInterval.Day1;
        public Task<BarSeries> GetBarsAsync(BarRequest r, CancellationToken ct = default) => Task.FromResult(new BarSeries
        {
            Symbol = r.Symbol, Interval = r.Interval, Source = id,
            Bars = Enumerable.Range(0, 10).Select(i => new Bar(new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero).AddDays(i), 1, 2 + i, 0.5, 1 + i, 100)).ToList(),
        });
    }

    /// <summary>A quote source that never answers: honours the token (like HttpGateway) or ignores it.</summary>
    private sealed class HangingQuotes(string id, bool ignoreToken = false) : IQuoteProvider
    {
        public int Calls;
        /// <summary>The answer the token-ignoring variant is stuck on; tests can fault it after the budget has expired.</summary>
        public TaskCompletionSource<IReadOnlyList<Quote>> Never { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => id;
        public string DisplayName => id;
        public DataCapability Capabilities => DataCapability.Quote;
        public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
        {
            Calls++;
            return ignoreToken ? Never.Task : Hang(ct);
        }
        private static async Task<IReadOnlyList<Quote>> Hang(CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return []; }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    private static Quote Q(string s, string src) => new() { Symbol = s, Price = 10, Source = src };
    /// <summary>A working source that has every symbol except <paramref name="lacking"/>.</summary>
    private static FakeQuotes Lacks(string id, string lacking) => new(id, s => s.Where(x => x != lacking).Select(x => Q(x, id)).ToList());
    private static int Asked(FakeQuotes f, string symbol) => f.Requests.Count(r => r.Contains(symbol));

    private static IReadOnlyList<Quote> Fail(IReadOnlyList<string> _) => throw new DataSourceException("x", DataErrorKind.Network, "SSL UNEXPECTED_EOF");

    private static FailoverRouter Router(params IDataProvider[] p) =>
        new(p, new HealthTracker(), new Dictionary<DataCapability, string[]>
        {
            [DataCapability.Quote] = p.Select(x => x.Id).ToArray(), [DataCapability.DailyBars] = p.Select(x => x.Id).ToArray(),
        });

    [Fact]
    public async Task Fails_over_to_next_source_and_records_health()
    {
        var a = new FakeQuotes("a", Fail);
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        var q = await r.GetQuotesAsync(["AAPL"]);
        Assert.Equal("b", Assert.Single(q).Source);
        Assert.Equal(1, r.Health.Get("a")!.Failures);
        Assert.Equal(1, r.Health.Get("b")!.Successes);
        Assert.Equal(HealthState.Down, r.Health.Get("a")!.State(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Fills_symbols_missing_from_primary_and_keeps_requested_order()
    {
        var a = new FakeQuotes("a", s => s.Where(x => x != "BRK.B").Select(x => Q(x, "a")).ToList());
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var q = await Router(a, b).GetQuotesAsync(["brk.b", "AAPL", "aapl"]);
        Assert.Equal([("BRK.B", "b"), ("AAPL", "a")], q.Select(x => (x.Symbol, x.Source)));
    }

    [Fact]
    public async Task Opens_circuit_after_repeated_failures_and_skips_source()
    {
        var a = new FakeQuotes("a", Fail);
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        for (var i = 0; i < HealthTracker.FailuresToOpen; i++) await r.GetQuotesAsync(["X"]);
        Assert.False(r.Health.IsAvailable("a"));
        await r.GetQuotesAsync(["X"]);
        Assert.Equal(HealthTracker.FailuresToOpen, a.Calls); // skipped while open
        Assert.NotNull(r.Health.Get("a")!.CircuitOpenUntil);
    }

    [Fact]
    public async Task Empty_results_do_not_trip_the_breaker()
    {
        var a = new FakeQuotes("a", _ => []);
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        for (var i = 0; i < 5; i++) await r.GetQuotesAsync(["X"]);
        Assert.True(r.Health.IsAvailable("a"));
        Assert.Equal(5, a.Calls); // an all-empty answer is not remembered as a miss: asked every time
    }

    [Fact]
    public async Task An_all_empty_answer_from_the_primary_remembers_nothing_so_it_is_asked_for_everything_next_time()
    {
        var hiccup = true;
        var a = new FakeQuotes("a", s => hiccup ? [] : s.Select(x => Q(x, "a")).ToList());
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        r.Now = () => T0;
        Assert.All(await r.GetQuotesAsync(["AAPL", "MSFT"]), q => Assert.Equal("b", q.Source));
        hiccup = false;
        Assert.All(await r.GetQuotesAsync(["AAPL", "MSFT"]), q => Assert.Equal("a", q.Source));
        Assert.Equal(["AAPL", "MSFT"], a.Requests[1]);
        Assert.True(r.Health.IsAvailable("a"));
    }

    [Fact]
    public async Task A_source_that_lacks_a_symbol_is_not_asked_again_until_the_retry_delay_passes()
    {
        var a = Lacks("a", "X");
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var now = T0;
        var r = Router(a, b);
        r.Now = () => now;
        for (var i = 0; i < 5; i++) Assert.Equal("b", (await r.GetQuotesAsync(["AAPL", "X"])).Single(q => q.Symbol == "X").Source);
        Assert.Equal(1, Asked(a, "X"));
        Assert.Equal(5, b.Calls);
        now = T0.AddMinutes(2).AddSeconds(1);
        await r.GetQuotesAsync(["AAPL", "X"]);
        Assert.Equal(2, Asked(a, "X"));
        Assert.True(r.Health.IsAvailable("a"));
    }

    [Fact]
    public async Task Retry_delay_doubles_per_consecutive_miss_and_is_capped_at_30_minutes()
    {
        var a = Lacks("a", "X");
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var now = T0;
        var r = Router(a, b);
        r.Now = () => now;
        await r.GetQuotesAsync(["AAPL", "X"]);                       // miss 1: retry after 2 min
        foreach (var minutes in new[] { 2, 4, 8, 16 })       // misses 2 to 5: waits of 2, 4, 8, 16 min, then 32 (capped to 30)
        {
            now = now.AddMinutes(minutes).AddSeconds(-1);
            var before = Asked(a, "X");
            await r.GetQuotesAsync(["AAPL", "X"]);
            Assert.Equal(before, Asked(a, "X"));                   // one second early: still skipped
            now = now.AddSeconds(2);
            await r.GetQuotesAsync(["AAPL", "X"]);
            Assert.Equal(before + 1, Asked(a, "X"));
        }
        var calls = Asked(a, "X");
        now = now.AddMinutes(29);
        await r.GetQuotesAsync(["AAPL", "X"]);
        Assert.Equal(calls, Asked(a, "X"));                        // 29 min < cap
        now = now.AddMinutes(1).AddSeconds(1);
        await r.GetQuotesAsync(["AAPL", "X"]);
        Assert.Equal(calls + 1, Asked(a, "X"));                    // 30 min is the longest wait
    }

    [Fact]
    public async Task A_hit_clears_the_miss_so_the_next_miss_starts_at_two_minutes_again()
    {
        var has = false;
        var a = new FakeQuotes("a", s => s.Where(x => has || x != "X").Select(x => Q(x, "a")).ToList());
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var now = T0;
        var r = Router(a, b);
        r.Now = () => now;
        await r.GetQuotesAsync(["AAPL", "X"]);                       // miss 1
        now = now.AddMinutes(2).AddSeconds(1);
        await r.GetQuotesAsync(["AAPL", "X"]);                       // miss 2 (delay now 4 min)
        now = now.AddMinutes(4).AddSeconds(1);
        has = true;
        Assert.Equal("a", (await r.GetQuotesAsync(["AAPL", "X"])).Single(q => q.Symbol == "X").Source); // hit: cleared
        has = false;
        var calls = Asked(a, "X");
        await r.GetQuotesAsync(["AAPL", "X"]);                       // asked at once (no memory), misses again: miss 1
        Assert.Equal(calls + 1, Asked(a, "X"));
        now = now.AddMinutes(2).AddSeconds(1);
        await r.GetQuotesAsync(["AAPL", "X"]);
        Assert.Equal(calls + 2, Asked(a, "X"));                    // 2 min, not the 8 min a third consecutive miss would need
    }

    [Fact]
    public async Task Remembered_miss_is_per_symbol_so_the_primary_is_still_asked_for_the_rest()
    {
        var a = new FakeQuotes("a", s => s.Where(x => x != "ZZZZ").Select(x => Q(x, "a")).ToList());
        var b = new FakeQuotes("b", s => s.Where(x => x != "ZZZZ").Select(x => Q(x, "b")).ToList());
        var c = new FakeQuotes("c", s => s.Where(x => x != "ZZZZ").Select(x => Q(x, "c")).ToList());
        var r = Router(a, b, c);
        r.Now = () => T0;
        var first = await r.GetQuotesAsync(["AAPL", "ZZZZ"]);
        Assert.Equal("AAPL", Assert.Single(first).Symbol);
        Assert.Equal((1, 1, 1), (a.Calls, b.Calls, c.Calls)); // every source was asked once for ZZZZ
        var second = await r.GetQuotesAsync(["AAPL", "ZZZZ"]);
        Assert.Equal("AAPL", Assert.Single(second).Symbol);
        Assert.Equal(2, a.Calls);
        Assert.Equal(["AAPL"], a.Requests[1]);               // primary: AAPL only
        Assert.Equal((1, 1), (b.Calls, c.Calls));            // nothing left to ask the others: not called
    }

    [Fact]
    public async Task Skipped_sources_get_no_health_record()
    {
        var a = new FakeQuotes("a", s => s.Where(x => x != "ZZZZ").Select(x => Q(x, "a")).ToList());
        var b = new FakeQuotes("b", _ => []);
        var r = Router(a, b);
        r.Now = () => T0;
        await r.GetQuotesAsync(["AAPL", "ZZZZ"]);
        var failures = r.Health.Get("b")!.Failures;
        var lastUsed = r.Health.Get("b")!.LastUsedAt;
        var lastErrorAt = r.Health.Get("b")!.LastErrorAt;
        await r.GetQuotesAsync(["AAPL", "ZZZZ"]);
        Assert.Equal(1, b.Calls);
        Assert.Equal(failures, r.Health.Get("b")!.Failures);
        Assert.Equal(lastUsed, r.Health.Get("b")!.LastUsedAt);
        Assert.Equal(lastErrorAt, r.Health.Get("b")!.LastErrorAt);
    }

    [Fact]
    public async Task All_symbols_remembered_as_missing_throws_without_asking_anyone()
    {
        var a = new FakeQuotes("a", s => s.Where(x => x == "AAPL").Select(x => Q(x, "a")).ToList());
        var b = new FakeQuotes("b", _ => []);
        var r = Router(a, b);
        r.Now = () => T0;
        await r.GetQuotesAsync(["AAPL", "X", "Y"]);          // a answered AAPL, so its and b's lack of X and Y is remembered
        Assert.Equal((1, 1), (a.Calls, b.Calls));
        var ex = await Assert.ThrowsAsync<AllSourcesFailedException>(() => r.GetQuotesAsync(["X", "Y"]));
        Assert.Equal((1, 1), (a.Calls, b.Calls));            // nobody asked the second time
        var (source, error) = Assert.Single(ex.Errors);
        Assert.Equal("router", source);
        Assert.Equal("no source has: X, Y", error);
    }

    [Fact]
    public async Task A_source_that_throws_records_no_miss_and_is_asked_again()
    {
        var a = new FakeQuotes("a", Fail);
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        r.Now = () => T0;
        await r.GetQuotesAsync(["X"]);
        await r.GetQuotesAsync(["X"]);
        Assert.Equal(2, a.Calls);
    }

    [Fact]
    public async Task ForgetMisses_makes_every_source_askable_again_at_once()
    {
        var a = Lacks("a", "X");
        var b = Lacks("b", "X");
        var c = new FakeQuotes("c", s => s.Select(x => Q(x, "c")).ToList());
        var r = Router(a, b, c);
        r.Now = () => T0;
        await r.GetQuotesAsync(["AAPL", "X"]);
        await r.GetQuotesAsync(["AAPL", "X"]);
        Assert.Equal((1, 1, 2), (Asked(a, "X"), Asked(b, "X"), Asked(c, "X")));
        r.ForgetMisses();
        await r.GetQuotesAsync(["AAPL", "X"]);
        Assert.Equal((2, 2, 3), (Asked(a, "X"), Asked(b, "X"), Asked(c, "X")));
    }

    [Fact]
    public async Task A_source_that_exceeds_the_quote_budget_is_a_hard_failure_and_the_chain_moves_on()
    {
        var a = new HangingQuotes("a");
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        r.QuoteSourceBudget = () => TimeSpan.FromMilliseconds(50);
        var q = await r.GetQuotesAsync(["AAPL"]).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("b", Assert.Single(q).Source);
        var h = r.Health.Get("a")!;
        Assert.Equal(1, h.Failures);
        Assert.Equal(1, h.ConsecutiveFailures);
        Assert.StartsWith("timed out after", h.LastError);
        Assert.Equal(1, r.Health.Get("b")!.Successes);
    }

    [Fact]
    public async Task The_quote_budget_also_bounds_a_source_that_ignores_its_token()
    {
        var a = new HangingQuotes("a", ignoreToken: true);
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        r.QuoteSourceBudget = () => TimeSpan.FromMilliseconds(50);
        var q = await r.GetQuotesAsync(["AAPL"]).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("b", Assert.Single(q).Source);
        Assert.Equal(1, r.Health.Get("a")!.Failures);
    }

    [Fact]
    public async Task A_source_abandoned_at_the_budget_that_faults_later_raises_no_unobserved_exception()
    {
        var marker = new InvalidOperationException("abandoned quote call failed");
        var seen = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => ReferenceEquals(x, marker))) seen.Add(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await AbandonAtBudgetThenFailAsync(marker);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.Empty(seen);
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    /// <summary>Kept out of line so nothing here keeps the abandoned call alive.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AbandonAtBudgetThenFailAsync(Exception boom)
    {
        var a = new HangingQuotes("a", ignoreToken: true);
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        r.QuoteSourceBudget = () => TimeSpan.FromMilliseconds(50);
        var q = await r.GetQuotesAsync(["AAPL"]).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("b", Assert.Single(q).Source);
        a.Never.SetException(boom); // the call the router gave up on fails afterwards
        await Task.Delay(100);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_quote_call_still_propagates_and_is_not_a_source_failure()
    {
        var a = new HangingQuotes("a");
        var b = new FakeQuotes("b", s => s.Select(x => Q(x, "b")).ToList());
        var r = Router(a, b);
        using var cts = new CancellationTokenSource();
        var t = r.GetQuotesAsync(["AAPL"], cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, r.Health.Get("a")!.Failures);
        Assert.Equal(0, b.Calls);
    }

    [Fact]
    public void The_quote_budget_follows_the_network_settings_timeout_times_attempts()
    {
        using var d = new DataServices(new NetworkSettings(), gateway: TestHttp.Gateway(new FakeHandler(), retries: 1));
        Assert.Equal(TimeSpan.FromSeconds(10), d.Router.QuoteSourceBudget());   // 5 s × (1 retry + 1)
        d.Gateway.Settings.RequestTimeoutSeconds = 30;
        d.Gateway.Settings.MaxRetries = 2;
        Assert.Equal(TimeSpan.FromSeconds(90), d.Router.QuoteSourceBudget());
    }

    [Fact]
    public async Task Throws_with_every_source_error_when_all_fail()
    {
        var ex = await Assert.ThrowsAsync<AllSourcesFailedException>(() => Router(new FakeQuotes("a", Fail), new FakeQuotes("b", Fail)).GetQuotesAsync(["X"]));
        Assert.Equal(["a", "b"], ex.Errors.Select(e => e.Source));
    }

    [Fact]
    public async Task Aggregates_weekly_bars_locally_from_daily()
    {
        var s = await Router(new FakeQuotes("a", Fail)).GetBarsAsync(new BarRequest("AAPL", BarInterval.Week1));
        Assert.Equal(BarInterval.Week1, s.Interval);
        Assert.Equal(2, s.Bars.Count); // Mon 2 Nov .. Wed 11 Nov 2026 → two ISO weeks
        Assert.Equal((1.0, 8.0, 7.0, 700.0), (s.Bars[0].Open, s.Bars[0].High, s.Bars[0].Close, s.Bars[0].Volume)); // 2–8 Nov
    }
}