using Meili.Core.Alerts;
using Meili.Core.Diagnostics;
using Meili.Core.Settings;
using Meili.Data.Cache;

namespace Meili.Shared.Services;

/// <summary>
/// B1: samples IV for symbols that have IV alerts every <see cref="Interval"/> (IV moves slowly and the option sources
/// are heavy), stores each reading in the local market database (iv_samples, later used for IV Rank) and evaluates the alerts.
/// </summary>
public sealed class IvAlertSampler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    /// <summary>Samples older than this are pruned (one year plus a margin, enough for a 52-week IV Rank).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);

    private readonly AlertStore _store;
    private readonly IIvSource _source;
    private readonly IAlertNotifier _notify;
    private readonly SettingsStore _settings;
    private readonly MarketDb _db;
    private readonly AppLaunchOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private string _symbolsKey = "";
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public event EventHandler? Updated;

    /// <summary>Latest reading per symbol (any age), for the alerts page.</summary>
    public IReadOnlyDictionary<string, IvSample> Latest => _latest;
    private readonly Dictionary<string, IvSample> _latest = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Symbols whose last attempt produced no reading.</summary>
    public IReadOnlyCollection<string> Failed => _failed;
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastRun { get; private set; }

    public IvAlertSampler(AlertStore store, IIvSource source, IAlertNotifier notify, SettingsStore settings, MarketDb db, AppLaunchOptions options)
    {
        _store = store; _source = source; _notify = notify; _settings = settings; _db = db; _options = options;
    }

    public void Start()
    {
        if (_options.NoAlerts || _cts is not null) return;
        _cts = new CancellationTokenSource();
        _symbolsKey = Key();
        _store.Changed += OnStoreChanged;
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (_cts is null) return;
        _store.Changed -= OnStoreChanged;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _store.Flush(); // C4
    }

    public void Dispose() => Stop();

    private string Key() => string.Join(",", _store.IvSymbols.Select(s => s.ToUpperInvariant()).Order());

    /// <summary>Sample right away when the set of IV symbols changes (not on every state write, which this class causes itself).</summary>
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        var key = Key();
        if (key == _symbolsKey || _cts is null) return;
        _symbolsKey = key;
        var token = _cts.Token;
        // Alert state and AlertStore.Changed handlers (which rebuild UI collections) belong to the UI context; only the raw SQLite calls go to the pool.
        Ui.Platform.Run(() => _ = SampleSafelyAsync(DateTimeOffset.Now, token));
    }

    /// <summary>Fire-and-forget safe: cancellation ends quietly, anything else is logged instead of going unobserved.</summary>
    private async Task SampleSafelyAsync(DateTimeOffset now, CancellationToken ct)
    {
        try { await SampleAsync(now, ct); }
        catch (OperationCanceledException) { } // expected: the sampler was stopped
        catch (Exception ex) { AppLog.Warn("iv", "IV sampling failed: " + ex.Message); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await SampleSafelyAsync(DateTimeOffset.Now, ct);
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(ct))
                await SampleSafelyAsync(DateTimeOffset.Now, ct);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One round over all IV-alert symbols. Rounds never overlap.</summary>
    internal async Task SampleAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var symbols = _store.IvSymbols;
        if (symbols.Count == 0) return;
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var samples = new Dictionary<string, IvSample>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in symbols)
            {
                ct.ThrowIfCancellationRequested();
                IvSample? sample = null;
                try { sample = await _source.GetAsync(symbol, now, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { AppLog.Warn("iv", $"IV sample for {symbol} failed: {ex.Message}"); }
                lock (_latest)
                {
                    if (sample is null) { _failed.Add(symbol); continue; }
                    _failed.Remove(symbol);
                    _latest[symbol] = sample;
                }
                samples[symbol] = sample;
                var s = sample;
                try { await Task.Run(() => _db.AddIvSample(symbol, s.AsOf ?? now, s.Basis.ToString(), s.Iv, s.Source), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { AppLog.Warn("iv", "store IV sample failed: " + ex.Message); }
            }
            Apply(samples, now);
            if (now - _lastPrune > TimeSpan.FromDays(1))
            {
                _lastPrune = now;
                try { await Task.Run(() => _db.PruneIvSamples(now - Retention)); } catch (Exception ex) { AppLog.Warn("iv", "prune failed: " + ex.Message); }
            }
            LastRun = now;
        }
        finally { _gate.Release(); }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    internal void Apply(IReadOnlyDictionary<string, IvSample> samples, DateTimeOffset now)
    {
        if (samples.Count == 0) return;
        var maxDelay = _settings.Current.Alerts.MaxQuoteDelayMinutes;
        var applied = new List<(PriceAlert Next, AlertHistoryEntry? History)>();
        foreach (var alert in _store.Alerts)
        {
            if (!alert.Enabled || !alert.Kind.IsIv() || !samples.TryGetValue(alert.Symbol, out var sample)) continue;
            if (IvSampleGate.Check(sample, now, maxDelay) == AlertQuoteUse.TooStale) continue;
            var result = AlertEvaluator.EvaluateIv(alert, sample, now);
            AlertHistoryEntry? history = null;
            if (result.Fired)
            {
                history = AlertEvaluator.HistoryFromIv(alert, result, sample, now);
                try { _notify.ShowIvAlert(alert, result, sample); }
                catch (Exception ex) { AppLog.Warn("iv", "toast failed: " + ex.Message); }
            }
            if (result.Fired || result.Next.HasSeenQuote != alert.HasSeenQuote || result.Next.LastSeenIv != alert.LastSeenIv
                || result.Next.LastSeenIvBasis != alert.LastSeenIvBasis || result.Next.Enabled != alert.Enabled)
                applied.Add((result.Next, history));
        }
        if (applied.Count > 0) _store.ApplyResults(applied);
    }
}
