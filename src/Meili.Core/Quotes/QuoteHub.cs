using Meili.Core.Abstractions;
using Meili.Core.Models;

namespace Meili.Core.Quotes;

/// <summary>One quote round-trip, broadcast to every listener.</summary>
public sealed record QuoteBatch(
    IReadOnlyList<string> Requested,
    IReadOnlyList<Quote> Quotes,
    Exception? Error,
    DateTimeOffset At)
{
    public bool Covers(string symbol) => Requested.Contains(symbol, StringComparer.OrdinalIgnoreCase);

    public Quote? Find(string symbol) =>
        Quotes.FirstOrDefault(q => string.Equals(q.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// C1: the single place quotes are fetched. Pages and the alert monitor <see cref="Subscribe"/> their symbols;
/// <see cref="PollAsync"/> (driven by one app-level timer) fetches the union in one request and raises
/// <see cref="Updated"/>. On-demand reads go through <see cref="GetQuotesAsync"/>, which serves quotes younger than
/// <see cref="Ttl"/> from memory, shares an identical in-flight request, and piggybacks other subscribers' stale
/// symbols onto the same request. Health counters feed F10 ("monitoring interrupted").
/// </summary>
/// <remarks>
/// Not thread-affine by itself; continuations resume on the caller's context (no ConfigureAwait(false)), so when the
/// UI thread drives it, <see cref="Updated"/> is raised on the UI thread.
/// </remarks>
public sealed class QuoteHub
{
    private readonly IMarketDataService _data;
    private readonly Dictionary<string, HashSet<string>> _subs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Quote Quote, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private (HashSet<string> Symbols, Task<QuoteBatch> Task)? _inflight;

    public QuoteHub(IMarketDataService data) => _data = data;

    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }
    public Exception? LastError { get; private set; }
    public int RequestCount { get; private set; }

    public event EventHandler<QuoteBatch>? Updated;

    public IReadOnlyList<string> SubscribedSymbols
    {
        get
        {
            lock (_gate)
                return _subs.Values.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Replaces <paramref name="owner"/>'s symbol set.</summary>
    public void Subscribe(string owner, IEnumerable<string> symbols)
    {
        var set = new HashSet<string>(symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);
        lock (_gate) _subs[owner] = set;
    }

    public void Unsubscribe(string owner)
    {
        lock (_gate) _subs.Remove(owner);
    }

    public bool TryGetCached(string symbol, out Quote quote)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(symbol, out var e)) { quote = e.Quote; return true; }
        }
        quote = null!;
        return false;
    }

    /// <summary>Timer tick: one request for every subscribed symbol. No-op without subscribers.</summary>
    public async Task<QuoteBatch?> PollAsync(CancellationToken ct = default)
    {
        var all = SubscribedSymbols;
        if (all.Count == 0) return null;
        return await FetchAsync(all).WaitAsync(ct);
    }

    /// <summary>
    /// Quotes for <paramref name="symbols"/>: fresh cache entries are returned as-is, the rest are fetched together with
    /// any stale subscribed symbols. Throws what the data service throws (after broadcasting the failure).
    /// </summary>
    public async Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        var wanted = symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return [];
        var now = Now();
        List<string> stale;
        lock (_gate) stale = wanted.Where(s => !IsFresh(s, now)).ToList();
        if (stale.Count > 0)
        {
            // Piggyback: other subscribers' symbols that are stale too ride along in the same request.
            var extra = SubscribedSymbols.Where(s => !wanted.Contains(s, StringComparer.OrdinalIgnoreCase));
            lock (_gate) extra = extra.Where(s => !IsFresh(s, now)).ToList();
            var batch = await FetchAsync(stale.Concat(extra).ToList()).WaitAsync(ct);
            if (batch.Error is not null) throw batch.Error;
        }
        lock (_gate)
            return wanted.Where(_cache.ContainsKey).Select(s => _cache[s].Quote).ToList();
    }

    private bool IsFresh(string symbol, DateTimeOffset now) =>
        _cache.TryGetValue(symbol, out var e) && now - e.At < Ttl;

    /// <summary>
    /// The shared fetch is detached from its callers: it is bounded only by <see cref="RequestTimeout"/>. Each caller
    /// awaits it with <c>WaitAsync(ct)</c>, so one caller giving up cannot cancel the request the others are sharing.
    /// </summary>
    private Task<QuoteBatch> FetchAsync(IReadOnlyList<string> symbols)
    {
        lock (_gate)
        {
            if (_inflight is { } f && !f.Task.IsCompleted && symbols.All(f.Symbols.Contains))
                return f.Task;
            var task = RunAsync(symbols);
            _inflight = (new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase), task);
            return task;
        }
    }

    private async Task<QuoteBatch> RunAsync(IReadOnlyList<string> symbols)
    {
        RequestCount++;
        QuoteBatch batch;
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            var quotes = await _data.GetQuotesAsync(symbols, timeout.Token);
            var at = Now();
            lock (_gate)
                foreach (var q in quotes.Where(q => !string.IsNullOrWhiteSpace(q.Symbol)))
                    _cache[q.Symbol] = (q, at);
            ConsecutiveFailures = 0;
            LastSuccess = at;
            LastError = null;
            batch = new QuoteBatch(symbols, quotes, null, at);
        }
        catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
        {
            throw; // cancelled by something other than our own timeout; not a source failure
        }
        catch (Exception ex)
        {
            ConsecutiveFailures++;
            LastError = ex;
            Diagnostics.AppLog.Warn("quotes", $"batch failed ({ConsecutiveFailures} in a row)", ex);
            batch = new QuoteBatch(symbols, [], ex, Now());
        }
        Updated?.Invoke(this, batch);
        return batch;
    }
}
