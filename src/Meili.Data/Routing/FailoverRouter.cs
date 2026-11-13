using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Data.Http;

namespace Meili.Data.Routing;

/// <summary>Raised when every source for a capability failed; carries each source's error for the UI.</summary>
public sealed class AllSourcesFailedException(DataCapability capability, IReadOnlyList<(string Source, string Error)> errors)
    : Exception($"{capability}: all sources failed — " + string.Join("; ", errors.Select(e => $"{e.Source}: {e.Error}")))
{
    public DataCapability Capability { get; } = capability;
    public IReadOnlyList<(string Source, string Error)> Errors { get; } = errors;
}

/// <summary>
/// Tries the sources for each capability in priority order, skipping sources whose circuit is open (unless nothing else is
/// left), validating results (empty / implausible data counts as a failure) and recording every outcome in <see cref="HealthTracker"/>.
/// </summary>
public sealed class FailoverRouter : IMarketDataService
{
    private readonly Dictionary<DataCapability, IReadOnlyList<IDataProvider>> _chains = [];
    public HealthTracker Health { get; }
    /// <summary>Clock used to stamp <see cref="SourcedData.FetchedAt"/> (replaceable in tests).</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public FailoverRouter(IEnumerable<IDataProvider> providers, HealthTracker health, IReadOnlyDictionary<DataCapability, string[]> order)
    {
        Health = health;
        var byId = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var p in byId.Values) health.Register(p);
        foreach (var (cap, ids) in order)
            _chains[cap] = ids.Where(byId.ContainsKey).Select(id => byId[id]).Where(p => p.Capabilities.HasFlag(cap)).ToList();
    }

    public IReadOnlyList<IDataProvider> Chain(DataCapability cap) => _chains.TryGetValue(cap, out var c) ? c : [];

    /// <summary>Available sources first (in order), then circuit-open ones as a last resort.</summary>
    private IEnumerable<T> Candidates<T>(DataCapability cap) where T : class
    {
        var chain = Chain(cap).OfType<T>().ToList();
        return chain.Where(p => Health.IsAvailable(((IDataProvider)p).Id)).Concat(chain.Where(p => !Health.IsAvailable(((IDataProvider)p).Id)));
    }

    private async Task<TResult> RunAsync<TProvider, TResult>(DataCapability cap, Func<TProvider, Task<TResult>> call,
        Func<TResult, bool> valid, CancellationToken ct) where TProvider : class
    {
        var errors = new List<(string, string)>();
        foreach (var p in Candidates<TProvider>(cap))
        {
            ct.ThrowIfCancellationRequested();
            var id = ((IDataProvider)p).Id;
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await call(p).ConfigureAwait(false);
                if (!valid(r)) { Fail(id, "empty or implausible result", hard: false, errors); continue; }
                Health.RecordSuccess(id, sw.Elapsed);
                return Stamp(r);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Fail(id, Describe(ex), IsHard(ex), errors); }
        }
        throw new AllSourcesFailedException(cap, errors);
    }

    /// <summary>F4: the download time goes into FetchedAt; AsOf stays whatever market time the source stated.</summary>
    private TResult Stamp<TResult>(TResult r) => r is SourcedData sd && sd.FetchedAt is null ? (TResult)(object)(sd with { FetchedAt = Now() }) : r;

    private void Fail(string id, string error, bool hard, List<(string, string)> errors)
    {
        Health.RecordFailure(id, error, hard);
        errors.Add((id, error));
    }

    private static bool IsHard(Exception ex) => ex is not DataSourceException { Kind: DataErrorKind.NoData or DataErrorKind.Unsupported };

    private static string Describe(Exception ex) => ex switch
    {
        DataSourceException d => d.Message,
        JsonException or FormatException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException => $"parse: {ex.Message}",
        OperationCanceledException => "timed out",
        _ => ex.Message,
    };

    // ---------------- quotes: fill missing symbols from the next source ----------------

    public async Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        var wanted = symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (wanted.Count == 0) return [];
        var got = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<(string, string)>();
        var asked = false;
        foreach (var p in Candidates<IQuoteProvider>(DataCapability.Quote))
        {
            var missing = wanted.Where(s => !got.ContainsKey(s)).ToList();
            if (missing.Count == 0) break;
            var ask = missing.Where(s => !IsRememberedMiss(p.Id, s)).ToList();
            if (ask.Count == 0) continue; // this source already said it has none of these: no request, no health record
            asked = true;
            var sw = Stopwatch.StartNew();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var limit = QuoteSourceBudget();
            budget.CancelAfter(limit);
            try
            {
                // WaitAsync bounds a source that ignores its token; the abandoned call is left to finish on its own.
                var all = await ObserveFault(p.GetQuotesAsync(ask, budget.Token)).WaitAsync(budget.Token).ConfigureAwait(false);
                var r = all.Where(q => q.Price > 0 && double.IsFinite(q.Price)).ToList();
                // An empty answer may be a hiccup (expired crumb, throttling), not "no such symbols": a miss counts only when this
                // source, or an earlier one in this round, returned other symbols.
                RecordAnswer(p.Id, ask, r, rememberMisses: r.Count > 0 || got.Count > 0);
                if (r.Count == 0) { Fail(p.Id, "no quotes returned", hard: false, errors); continue; }
                var fetched = Now();
                foreach (var q in r) got.TryAdd(q.Symbol, q with { FetchedAt = q.FetchedAt ?? fetched });
                Health.RecordSuccess(p.Id, sw.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                Fail(p.Id, $"timed out after {limit.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s", hard: true, errors);
            }
            catch (Exception ex) { Fail(p.Id, Describe(ex), IsHard(ex), errors); }
        }
        if (got.Count == 0)
        {
            if (!asked) errors.Add(("router", $"no source has: {string.Join(", ", wanted)}"));
            throw new AllSourcesFailedException(DataCapability.Quote, errors);
        }
        return wanted.Where(got.ContainsKey).Select(s => got[s]).ToList();
    }

    /// <summary>A call abandoned at the budget may fail later; read its exception so it never surfaces as unobserved.</summary>
    private static Task<T> ObserveFault<T>(Task<T> task)
    {
        _ = task.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    // ---------------- miss memory: a source that answered without a symbol is not asked for it again for a while ----------------

    private static readonly TimeSpan MissRetryBase = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MissRetryMax = TimeSpan.FromMinutes(30);
    private readonly Dictionary<(string Source, string Symbol), (DateTimeOffset RetryAfter, int Misses)> _misses = [];
    private readonly object _missGate = new();

    /// <summary>
    /// Longest a single source may take to answer one quote request before the chain moves on (quotes only). <see cref="DataServices"/>
    /// derives it from the network settings (request timeout × (retries + 1)); the default matches the default settings.
    /// </summary>
    public Func<TimeSpan> QuoteSourceBudget { get; set; } = () => TimeSpan.FromSeconds(45);

    /// <summary>Forget what the sources lacked (a proxy change invalidates it); every source is askable for every symbol again.</summary>
    public void ForgetMisses()
    {
        lock (_missGate) _misses.Clear();
    }

    private bool IsRememberedMiss(string source, string symbol)
    {
        lock (_missGate) return _misses.TryGetValue((source.ToUpperInvariant(), symbol), out var m) && m.RetryAfter > Now();
    }

    /// <summary>A call that returned without throwing: symbols it gave are cleared; when <paramref name="rememberMisses"/>, symbols it lacked start (or lengthen) a miss.</summary>
    private void RecordAnswer(string source, IReadOnlyList<string> asked, IReadOnlyList<Quote> answer, bool rememberMisses)
    {
        var have = answer.Select(q => q.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = Now();
        var key = source.ToUpperInvariant();
        lock (_missGate)
            foreach (var s in asked)
            {
                if (have.Contains(s)) { _misses.Remove((key, s)); continue; }
                if (!rememberMisses) continue;
                var misses = _misses.TryGetValue((key, s), out var m) ? m.Misses + 1 : 1;
                var minutes = Math.Min(MissRetryMax.TotalMinutes, MissRetryBase.TotalMinutes * Math.Pow(2, Math.Min(misses - 1, 10)));
                _misses[(key, s)] = (now + TimeSpan.FromMinutes(minutes), misses);
            }
    }

    // ---------------- bars: aggregate locally when a source lacks the interval ----------------

    public Task<BarSeries> GetBarsAsync(BarRequest request, CancellationToken ct = default)
    {
        var cap = request.Interval.IsIntraday() ? DataCapability.IntradayBars : DataCapability.DailyBars;
        return RunAsync<IBarsProvider, BarSeries>(cap, p => FetchBarsAsync(p, request, ct), s => s.Bars.Count > 0 && s.Bars.All(b => b.Close > 0), ct);
    }

    internal static async Task<BarSeries> FetchBarsAsync(IBarsProvider p, BarRequest r, CancellationToken ct)
    {
        if (p.Supports(r.Interval)) return await p.GetBarsAsync(r, ct).ConfigureAwait(false);
        var basis = r.Interval.IsIntraday() ? BarInterval.Minute1 : BarInterval.Day1;
        if (r.Interval == basis || !p.Supports(basis)) throw new DataSourceException(p.Id, DataErrorKind.Unsupported, $"interval {r.Interval}");
        var fine = await p.GetBarsAsync(r with { Interval = basis }, ct).ConfigureAwait(false);
        return fine with { Interval = r.Interval, Bars = BarAggregator.Aggregate(fine.Bars, r.Interval) };
    }

    // ---------------- everything else ----------------

    public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string symbol, CancellationToken ct = default) =>
        RunAsync<IOptionsProvider, IReadOnlyList<DateOnly>>(DataCapability.Options, p => p.GetExpirationsAsync(symbol, ct), r => r.Count > 0, ct);

    public Task<OptionChain> GetChainAsync(string symbol, DateOnly? expiration = null, CancellationToken ct = default) =>
        RunAsync<IOptionsProvider, OptionChain>(DataCapability.Options, p => p.GetChainAsync(symbol, expiration, ct),
            c => c.Contracts.Count > 0 && (expiration is null || c.Contracts.Any(x => x.Expiration == expiration)), ct);

    public Task<Fundamentals> GetFundamentalsAsync(string symbol, CancellationToken ct = default) =>
        RunAsync<IFundamentalsProvider, Fundamentals>(DataCapability.Fundamentals, p => p.GetFundamentalsAsync(symbol, ct),
            f => f.Name is not null || f.MarketCap is not null, ct);

    public Task<FinancialStatements> GetFinancialsAsync(string symbol, CancellationToken ct = default) =>
        RunAsync<IFinancialsProvider, FinancialStatements>(DataCapability.Financials, p => p.GetFinancialsAsync(symbol, ct), f => f.Lines.Count > 0, ct);

    public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, int count = 20, CancellationToken ct = default) =>
        RunAsync<INewsProvider, IReadOnlyList<NewsItem>>(DataCapability.News, p => p.GetNewsAsync(symbol, count, ct), n => n.Count > 0, ct);

    public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 10, CancellationToken ct = default) =>
        RunAsync<ISymbolSearchProvider, IReadOnlyList<SymbolMatch>>(DataCapability.Search, p => p.SearchAsync(query, limit, ct), r => r.Count > 0, ct);

    // ---------------- status page: probe one source directly ----------------

    /// <summary>Runs a cheap representative request against one source (bypassing failover) and records the outcome.</summary>
    public async Task ProbeAsync(IDataProvider p, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ok = p switch
            {
                IQuoteProvider q => (await q.GetQuotesAsync(["AAPL"], ct).ConfigureAwait(false)).Count > 0,
                IFinancialsProvider f => (await f.GetFinancialsAsync("AAPL", ct).ConfigureAwait(false)).Lines.Count > 0,
                ISymbolSearchProvider s => (await s.SearchAsync("AAPL", 5, ct).ConfigureAwait(false)).Count > 0,
                IOptionsProvider o => (await o.GetExpirationsAsync("AAPL", ct).ConfigureAwait(false)).Count > 0,
                _ => true,
            };
            if (ok) Health.RecordSuccess(p.Id, sw.Elapsed); else Health.RecordFailure(p.Id, "empty result");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Health.RecordFailure(p.Id, Describe(ex)); }
    }
}