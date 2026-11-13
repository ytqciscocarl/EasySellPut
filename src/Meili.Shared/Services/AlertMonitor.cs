using Meili.Core.Alerts;
using Meili.Core.Diagnostics;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;

namespace Meili.Shared.Services;

/// <summary>
/// Evaluates enabled alerts against quotes from the shared <see cref="QuoteHub"/> (C1): it subscribes the alert symbols
/// and reacts to every batch the app-level <see cref="QuotePump"/> fetches, so it keeps working while the window is
/// hidden in the tray without a timer or request of its own.
/// </summary>
public sealed class AlertMonitor : IDisposable
{
    private const string Owner = "alerts";
    private readonly AlertStore _store;
    private readonly QuoteHub _hub;
    private readonly IAlertNotifier _notify;
    private readonly AppLaunchOptions _options;
    private readonly SettingsStore _settings;
    private bool _started;
    private int _batches;

    public event EventHandler? Updated;
    /// <summary>F10: monitoring became interrupted or recovered, or a check skipped stale quotes.</summary>
    public event EventHandler? HealthChanged;

    public MonitorHealth Health { get; }
    public AlertCheckSummary? LastCheck { get; private set; }
    public bool IsRunning => _started;

    public AlertMonitor(AlertStore store, QuoteHub hub, IAlertNotifier notify, AppLaunchOptions options, SettingsStore settings)
    {
        _store = store;
        _hub = hub;
        _notify = notify;
        _options = options;
        _settings = settings;
        Health = new MonitorHealth(settings.Current.Alerts.FailureThreshold);
    }

    public void Start()
    {
        if (_options.NoAlerts || _started) return;
        _started = true;
        _store.Changed += OnStoreChanged;
        _hub.Updated += OnBatch;
        Resubscribe();
        _ = PollAsync();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _store.Changed -= OnStoreChanged;
        _hub.Updated -= OnBatch;
        _hub.Unsubscribe(Owner);
        _store.Flush(); // C4: persist in-memory LastSeen* state
    }

    public void Dispose() => Stop();

    private void OnStoreChanged(object? sender, EventArgs e) => Resubscribe();

    private void Resubscribe() => _hub.Subscribe(Owner, _store.EnabledSymbols);

    /// <summary>Immediate check (start-up, or after alerts change). Errors are reported through the hub's batch event.</summary>
    public async Task PollAsync()
    {
        if (_options.NoAlerts) return;
        var symbols = _store.EnabledSymbols;
        if (symbols.Count == 0) return;
        var before = _batches;
        try
        {
            var quotes = await _hub.GetQuotesAsync(symbols);
            // Served from the hub's cache (no batch event fired): evaluate here so a manual re-check always runs.
            if (_batches == before && _started) Evaluate(quotes, DateTimeOffset.Now);
        }
        catch (Exception ex) { AppLog.Warn("alerts", "quote fetch failed: " + ex.Message); }
    }

    private void OnBatch(object? sender, QuoteBatch batch)
    {
        _batches++;
        if (!_started) return;
        OnBatchCore(batch, DateTimeOffset.Now);
    }

    /// <summary>F10: a batch that was meant to cover alert symbols but brought none of them back counts as a failed check.</summary>
    internal void OnBatchCore(QuoteBatch batch, DateTimeOffset now)
    {
        var mine = _store.EnabledSymbols;
        if (!mine.Any(batch.Covers)) return;
        if (batch.Error is not null || !mine.Any(s => batch.Find(s) is not null))
        {
            var error = batch.Error?.Message ?? "no quotes for alert symbols";
            Health.Threshold = _settings.Current.Alerts.FailureThreshold;
            var t = Health.RecordFailure(now, error);
            AppLog.Warn("alerts", $"check failed ({Health.ConsecutiveFailures}): {error}");
            if (t == MonitorTransition.Interrupted)
            {
                AppLog.Error("alerts", $"monitoring interrupted after {Health.ConsecutiveFailures} failed checks since {Health.FailingSince:O}");
                _notify.ShowMonitorInterrupted(Health);
            }
            HealthChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (Health.RecordSuccess(now) == MonitorTransition.Recovered)
        {
            AppLog.Info("alerts", "monitoring recovered");
            _notify.ShowMonitorRecovered();
        }
        Evaluate(batch.Quotes, now);
    }

    internal void Evaluate(IReadOnlyList<Quote> quotes, DateTimeOffset now)
    {
        var bySymbol = quotes
            .Where(q => !string.IsNullOrWhiteSpace(q.Symbol))
            .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (bySymbol.Count == 0) return;

        // F3: quotes known to be older than the limit are not evaluated at all; unknown delay is evaluated but labelled.
        var maxDelay = _settings.Current.Alerts.MaxQuoteDelayMinutes;
        var stale = new List<string>();
        var unknown = new List<string>();
        var evaluated = 0;
        var applied = new List<(PriceAlert Next, AlertHistoryEntry? History)>();
        foreach (var alert in _store.Alerts)
        {
            if (!alert.Enabled || alert.Kind.IsIv()) continue;
            if (!bySymbol.TryGetValue(alert.Symbol, out var quote)) continue;
            var (use, _) = AlertQuoteGate.Check(quote, now, maxDelay);
            if (use == AlertQuoteUse.TooStale)
            {
                if (!stale.Contains(alert.Symbol)) stale.Add(alert.Symbol);
                continue;
            }
            if (use == AlertQuoteUse.UsableDelayUnknown && !unknown.Contains(alert.Symbol)) unknown.Add(alert.Symbol);
            evaluated++;
            var result = AlertEvaluator.Evaluate(alert, quote, now);
            AlertHistoryEntry? history = null;
            if (result.Fired)
            {
                history = AlertEvaluator.HistoryFrom(alert, result, quote, now);
                try { _notify.ShowAlert(alert, result, quote); }
                catch (Exception ex) { AppLog.Warn("alerts", "toast failed: " + ex.Message); }
            }
            if (result.Fired || result.Next.HasSeenQuote != alert.HasSeenQuote
                || result.Next.LastSeenPrice != alert.LastSeenPrice
                || result.Next.LastSeenChangePercent != alert.LastSeenChangePercent
                || result.Next.LastSeenVolume != alert.LastSeenVolume
                || result.Next.LastSeenExtended != alert.LastSeenExtended
                || result.Next.Enabled != alert.Enabled
                || result.Next.LastFiredAt != alert.LastFiredAt)
                applied.Add((result.Next, history));
        }

        if (applied.Count > 0)
            _store.ApplyResults(applied);
        var hadStale = LastCheck?.SkippedStale.Count > 0;
        LastCheck = new AlertCheckSummary(now, evaluated, stale, unknown);
        if (stale.Count > 0) AppLog.Info("alerts", $"skipped stale quotes (> {maxDelay} min): {string.Join(",", stale)}");
        Updated?.Invoke(this, EventArgs.Empty);
        if (stale.Count > 0 || hadStale) HealthChanged?.Invoke(this, EventArgs.Empty);
    }
}
