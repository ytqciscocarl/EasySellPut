using System.Text.Json;
using System.Text.Json.Serialization;
using Meili.Core.Alerts;

namespace Meili.Core.Settings;

/// <summary>Loads and saves alert rules plus a capped fire history as JSON, with atomic writes.</summary>
public sealed class AlertStore
{
    public const int MaxHistory = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private readonly SnapshotFile _snapshot;
    public string FilePath { get; }
    public AlertDocument Current { get; private set; } = new();

    public event EventHandler? Changed;

    /// <summary>
    /// C4: evaluator bookkeeping (LastSeen*, HasSeenQuote) changes on every quote batch. Writing alerts.json each time
    /// wore the disk for nothing, so those updates stay in memory and are flushed at most every <see cref="FlushInterval"/>
    /// (and on shutdown). Fires, rule edits and history changes are still written immediately.
    /// </summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(2);

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>True when in-memory bookkeeping has not been written yet.</summary>
    public bool IsDirty { get { lock (_gate) return _dirtySince is not null; } }

    /// <summary>Number of times alerts.json was written (diagnostics / tests).</summary>
    public int WriteCount { get; private set; }

    private DateTimeOffset? _dirtySince;

    public AlertStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        FilePath = Path.Combine(dataDirectory, "alerts.json");
        _snapshot = new SnapshotFile(FilePath);
    }

    public AlertDocument Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var text = _snapshot.Read()!;
                    Current = Normalize(JsonSerializer.Deserialize<AlertDocument>(text, Json) ?? new AlertDocument());
                    return Current;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                Diagnostics.AppLog.Error("alerts", $"{FilePath} is unreadable, starting from defaults (copy kept as .corrupt-*)", ex);
                try { File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); }
                catch (Exception copyEx) { Diagnostics.AppLog.Warn("alerts", "could not keep a copy of the corrupt file", copyEx); }
            }
            _snapshot.Read();
            Current = new AlertDocument();
            return Current;
        }
    }

    public void Save(AlertDocument? document = null)
    {
        lock (_gate)
        {
            Current = Normalize(document ?? Current);
            _snapshot.Write(JsonSerializer.Serialize(Current, Json));
            _dirtySince = null;
            WriteCount++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>C4: write pending bookkeeping now (shutdown, monitor stop). No-op when clean.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_dirtySince is null) return;
            WriteQuietly();
        }
    }

    /// <summary>C4: write pending bookkeeping once it has waited <see cref="FlushInterval"/>.</summary>
    public bool FlushIfDue()
    {
        lock (_gate)
        {
            if (_dirtySince is not { } since || Clock() - since < FlushInterval) return false;
            WriteQuietly();
            return true;
        }
    }

    // Bookkeeping-only write: no Changed event (nothing a listener needs to re-read), errors logged not thrown.
    private void WriteQuietly()
    {
        try
        {
            _snapshot.Write(JsonSerializer.Serialize(Current, Json));
            _dirtySince = null;
            WriteCount++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Warn("alerts", "could not flush alert state, will retry", ex);
        }
    }

    public IReadOnlyList<PriceAlert> Alerts
    {
        get { lock (_gate) return Current.Alerts.ToList(); }
    }

    public IReadOnlyList<AlertHistoryEntry> History
    {
        get { lock (_gate) return Current.History.ToList(); }
    }

    /// <summary>B1: symbols that have an enabled IV alert (sampled by the IV sampler, not the quote stream).</summary>
    public IReadOnlyList<string> IvSymbols
    {
        get
        {
            lock (_gate)
            {
                return Current.Alerts
                    .Where(a => a.Enabled && a.Kind.IsIv() && !string.IsNullOrWhiteSpace(a.Symbol))
                    .Select(a => a.Symbol)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    /// <summary>Symbols with an enabled price / change / volume alert (quote-driven).</summary>
    public IReadOnlyList<string> EnabledSymbols
    {
        get
        {
            lock (_gate)
            {
                return Current.Alerts
                    .Where(a => a.Enabled && !a.Kind.IsIv() && !string.IsNullOrWhiteSpace(a.Symbol))
                    .Select(a => a.Symbol)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public PriceAlert? Find(string id)
    {
        lock (_gate) return Current.Alerts.FirstOrDefault(a => a.Id == id)?.Clone();
    }

    public PriceAlert Upsert(PriceAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        PriceAlert stored;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(alert.Id)) alert.Id = PriceAlert.NewId();
            var copy = NormalizeAlert(alert.Clone());
            var i = Current.Alerts.FindIndex(a => a.Id == copy.Id);
            if (i >= 0) Current.Alerts[i] = copy;
            else Current.Alerts.Add(copy);
            stored = copy.Clone();
        }
        Save();
        return stored;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate) removed = Current.Alerts.RemoveAll(a => a.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var a = Current.Alerts.FirstOrDefault(x => x.Id == id);
            if (a is null || a.Enabled == enabled) return;
            a.Enabled = enabled;
            if (enabled)
            {
                // Re-arm: require a fresh sample so a still-crossed level does not fire immediately.
                a.HasSeenQuote = false;
            }
        }
        Save();
    }

    /// <summary>Apply evaluator state and optionally append history, then persist once.</summary>
    public void ApplyResults(IReadOnlyList<(PriceAlert Next, AlertHistoryEntry? History)> results)
    {
        if (results.Count == 0) return;
        var important = false;
        var touched = false;
        lock (_gate)
        {
            foreach (var (next, history) in results)
            {
                var i = Current.Alerts.FindIndex(a => a.Id == next.Id);
                if (i < 0) continue;
                var before = Current.Alerts[i];
                if (history is not null || before.Enabled != next.Enabled || before.LastFiredAt != next.LastFiredAt)
                    important = true;
                Current.Alerts[i] = NormalizeAlert(next.Clone());
                if (history is not null) Current.History.Insert(0, history);
                touched = true;
            }
            if (important) TrimHistory(Current);
            else if (touched) _dirtySince ??= Clock();
        }
        if (important) Save();
        else if (touched) FlushIfDue();
    }

    public void ClearHistory()
    {
        lock (_gate) Current.History.Clear();
        Save();
    }

    internal static AlertDocument Normalize(AlertDocument doc)
    {
        doc.Version = AlertDocument.CurrentVersion;
        doc.Alerts ??= [];
        doc.History ??= [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<PriceAlert>();
        foreach (var raw in doc.Alerts)
        {
            if (raw is null) continue;
            var a = NormalizeAlert(raw);
            if (string.IsNullOrWhiteSpace(a.Symbol)) continue;
            if (string.IsNullOrWhiteSpace(a.Id) || !seen.Add(a.Id))
            {
                a.Id = PriceAlert.NewId();
                seen.Add(a.Id);
            }
            clean.Add(a);
        }
        doc.Alerts = clean;
        TrimHistory(doc);
        return doc;
    }

    internal static PriceAlert NormalizeAlert(PriceAlert a)
    {
        a.Id = string.IsNullOrWhiteSpace(a.Id) ? PriceAlert.NewId() : a.Id.Trim();
        a.Symbol = (a.Symbol ?? "").Trim().ToUpperInvariant();
        if (!Enum.IsDefined(a.Kind)) a.Kind = AlertKind.PriceAbove;
        if (!Enum.IsDefined(a.Repeat)) a.Repeat = AlertRepeatMode.Once;
        a.CooldownMinutes = Math.Clamp(a.CooldownMinutes <= 0 ? AlertEvaluator.DefaultCooldownMinutes : a.CooldownMinutes,
            AlertEvaluator.MinCooldownMinutes, AlertEvaluator.MaxCooldownMinutes);
        a.Note ??= "";
        if (double.IsNaN(a.Threshold) || double.IsInfinity(a.Threshold)) a.Threshold = 0;
        return a;
    }

    private static void TrimHistory(AlertDocument doc)
    {
        doc.History = doc.History
            .Where(h => h is not null && !string.IsNullOrWhiteSpace(h.Symbol))
            .OrderByDescending(h => h.FiredAt)
            .Take(MaxHistory)
            .ToList();
    }
}