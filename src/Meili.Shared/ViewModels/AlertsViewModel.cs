using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meili.Shared.Services;
using Meili.Core.Alerts;
using Meili.Core.Settings;

namespace Meili.Shared.ViewModels;

public sealed class KindOption
{
    public AlertKind Kind { get; init; }
    public string Title { get; init; } = "";
}

public sealed class RepeatOption
{
    public AlertRepeatMode Mode { get; init; }
    public string Title { get; init; } = "";
}

public partial class AlertRowViewModel : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] public partial string Symbol { get; set; } = "";
    [ObservableProperty] public partial string ConditionText { get; set; } = "";
    [ObservableProperty] public partial string ThresholdText { get; set; } = "";
    [ObservableProperty] public partial string RepeatText { get; set; } = "";
    [ObservableProperty] public partial string LastFiredText { get; set; } = "";
    [ObservableProperty] public partial string Note { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; }
    public PriceAlert Source { get; set; }

    public AlertRowViewModel(PriceAlert source)
    {
        Id = source.Id;
        Source = source;
    }
}

public partial class HistoryRowViewModel : ObservableObject
{
    [ObservableProperty] public partial string Symbol { get; set; } = "";
    [ObservableProperty] public partial string ConditionText { get; set; } = "";
    [ObservableProperty] public partial string ValueText { get; set; } = "";
    [ObservableProperty] public partial string TimeText { get; set; } = "";
    [ObservableProperty] public partial string SourceText { get; set; } = "";
}

public partial class AlertsViewModel : ObservableObject
{
    private readonly AlertStore _store;
    private readonly WatchlistStore _watchlist;
    private readonly LocalizationService _loc;
    private readonly AlertMonitor _monitor;
    private bool _suppressToggle;

    public ObservableCollection<string> WatchSymbols { get; } = [];
    public ObservableCollection<KindOption> KindOptions { get; } = [];
    public ObservableCollection<RepeatOption> RepeatOptions { get; } = [];
    public ObservableCollection<AlertRowViewModel> Rows { get; } = [];
    public ObservableCollection<HistoryRowViewModel> History { get; } = [];

    [ObservableProperty] public partial string SymbolText { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIvKind), nameof(IsQuoteKind), nameof(ThresholdHint))]
    public partial KindOption? SelectedKind { get; set; }
    /// <summary>B1: IV alerts are sampled every 5 minutes; their threshold is in percent and extended hours do not apply.</summary>
    public bool IsIvKind => SelectedKind?.Kind.IsIv() == true;
    public bool IsQuoteKind => !IsIvKind;
    public string ThresholdHint => IsIvKind ? _loc["Al_IvThresholdHint"] : _loc["Al_ThresholdHint"];
    [ObservableProperty] public partial bool HasIvNote { get; set; }
    [ObservableProperty] public partial string IvNote { get; set; } = "";
    [ObservableProperty] public partial string ThresholdText { get; set; } = "";
    [ObservableProperty] public partial bool IncludeExtendedHours { get; set; } = true;
    [ObservableProperty] public partial RepeatOption? SelectedRepeat { get; set; }
    [ObservableProperty] public partial string CooldownText { get; set; } = "15";
    [ObservableProperty] public partial string NoteText { get; set; } = "";
    [ObservableProperty] public partial AlertRowViewModel? SelectedRow { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial bool HistoryEmpty { get; set; } = true;
    [ObservableProperty] public partial bool MonitorDown { get; set; }
    [ObservableProperty] public partial string MonitorText { get; set; } = "";
    [ObservableProperty] public partial bool HasStaleNote { get; set; }
    [ObservableProperty] public partial string StaleNote { get; set; } = "";
    [ObservableProperty] public partial int MaxDelayMinutes { get; set; }
    public IReadOnlyList<int> MaxDelayChoices { get; } = [5, 10, 15, 20, 30, 60];

    private readonly SettingsStore _settings;
    private readonly IvAlertSampler? _iv;

    public AlertsViewModel(AlertStore store, WatchlistStore watchlist, LocalizationService loc, AlertMonitor monitor, SettingsStore settings,
        IvAlertSampler? iv = null)
    {
        _iv = iv;
        if (iv is not null) iv.Updated += (_, _) => Ui.Platform.Run(RefreshIvNote);
        _store = store;
        _watchlist = watchlist;
        _loc = loc;
        _monitor = monitor;
        _settings = settings;
        MaxDelayMinutes = settings.Current.Alerts.MaxQuoteDelayMinutes;
        _monitor.HealthChanged += (_, _) => Ui.Platform.Run(RefreshMonitorState);
        RebuildOptions();
        RebuildWatchSymbols();
        Reload();
        _watchlist.Changed += (_, _) => RebuildWatchSymbols();
        _store.Changed += (_, _) => Ui.Platform.Run(() =>
        {
            var keep = SelectedRow?.Id;
            Reload();
            RefreshIvNote();
            if (keep is not null)
                SelectedRow = Rows.FirstOrDefault(r => r.Id == keep);
        });
        _loc.LanguageChanged += (_, _) =>
        {
            var kind = SelectedKind?.Kind;
            var repeat = SelectedRepeat?.Mode;
            RebuildOptions();
            SelectedKind = KindOptions.FirstOrDefault(k => k.Kind == kind) ?? KindOptions.FirstOrDefault();
            SelectedRepeat = RepeatOptions.FirstOrDefault(r => r.Mode == repeat) ?? RepeatOptions.FirstOrDefault();
            Reload();
            RefreshMonitorState();
            RefreshIvNote();
            OnPropertyChanged(nameof(ThresholdHint));
        };
        RefreshMonitorState();
        RefreshIvNote();
    }

    /// <summary>B1: latest IV reading per IV-alert symbol, with what it measured, or that sampling failed.</summary>
    public void RefreshIvNote()
    {
        var symbols = _store.IvSymbols;
        if (_iv is null || symbols.Count == 0) { HasIvNote = false; IvNote = ""; return; }
        var parts = new List<string>();
        foreach (var s in symbols)
        {
            if (_iv.Failed.Contains(s)) parts.Add(string.Format(_loc["Al_IvFailed"], s));
            else if (_iv.Latest.TryGetValue(s, out var sample))
                parts.Add($"{s} {sample.Percent.ToString("0.0", CultureInfo.InvariantCulture)}% ({AlertText.IvBasisText(sample, _loc)})");
            else parts.Add(string.Format(_loc["Al_IvPending"], s));
        }
        IvNote = _loc["Al_IvNotePrefix"] + " " + string.Join("; ", parts);
        HasIvNote = true;
    }

    partial void OnMaxDelayMinutesChanged(int value)
    {
        if (value <= 0 || _settings.Current.Alerts.MaxQuoteDelayMinutes == value) return;
        _settings.Update(s => s.Alerts.MaxQuoteDelayMinutes = value);
    }

    /// <summary>F10 banner and F3 note, from the monitor's health and last check.</summary>
    public void RefreshMonitorState()
    {
        var h = _monitor.Health;
        MonitorDown = h.Interrupted;
        MonitorText = h.Interrupted
            ? string.Format(_loc["Al_MonitorDownBanner"], h.ConsecutiveFailures,
                h.FailingSince?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "", h.LastError ?? "")
            : "";
        var stale = _monitor.LastCheck?.SkippedStale ?? [];
        HasStaleNote = stale.Count > 0;
        StaleNote = stale.Count > 0
            ? string.Format(_loc["Al_StaleSkipped"], string.Join(", ", stale), _settings.Current.Alerts.MaxQuoteDelayMinutes)
            : "";
    }

    public void PrepareForSymbol(string? symbol)
    {
        if (WatchlistStore.TryNormalizeSymbol(symbol, out var s, out _))
            SymbolText = s;
        SelectedRow = null;
        HasSelection = false;
        ThresholdText = "";
        NoteText = "";
        IncludeExtendedHours = true;
        SelectedKind = KindOptions.FirstOrDefault();
        SelectedRepeat = RepeatOptions.FirstOrDefault();
        CooldownText = AlertEvaluator.DefaultCooldownMinutes.ToString();
        StatusText = string.Format(_loc["Al_Prepared"], s ?? "");
    }

    [RelayCommand]
    private void Add()
    {
        if (!TryReadDraft(out var alert, out var error))
        {
            StatusText = error;
            return;
        }
        alert.Id = PriceAlert.NewId();
        alert.CreatedAt = DateTimeOffset.Now;
        alert.Enabled = true;
        _store.Upsert(alert);
        StatusText = string.Format(_loc["Al_Added"], alert.Symbol);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == alert.Id);
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedRow is null)
        {
            Add();
            return;
        }
        if (!TryReadDraft(out var alert, out var error))
        {
            StatusText = error;
            return;
        }
        alert.Id = SelectedRow.Id;
        var existing = _store.Find(alert.Id);
        if (existing is not null)
        {
            alert.CreatedAt = existing.CreatedAt;
            alert.LastFiredAt = existing.LastFiredAt;
            alert.LastSeenPrice = existing.LastSeenPrice;
            alert.LastSeenChangePercent = existing.LastSeenChangePercent;
            alert.LastSeenVolume = existing.LastSeenVolume;
            alert.LastSeenIv = existing.LastSeenIv;
            alert.LastSeenIvBasis = existing.LastSeenIvBasis;
            alert.HasSeenQuote = existing.HasSeenQuote &&
                existing.Kind == alert.Kind &&
                existing.Threshold == alert.Threshold &&
                existing.IncludeExtendedHours == alert.IncludeExtendedHours &&
                string.Equals(existing.Symbol, alert.Symbol, StringComparison.OrdinalIgnoreCase);
            alert.Enabled = existing.Enabled;
        }
        _store.Upsert(alert);
        StatusText = string.Format(_loc["Al_Saved"], alert.Symbol);
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedRow is null) return;
        _store.Remove(SelectedRow.Id);
        SelectedRow = null;
        HasSelection = false;
        StatusText = _loc["Al_Removed"];
    }

    [RelayCommand]
    private void ToggleEnabled(AlertRowViewModel? row)
    {
        if (row is null || _suppressToggle) return;
        _store.SetEnabled(row.Id, row.Enabled);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _store.ClearHistory();
        StatusText = _loc["Al_HistoryCleared"];
    }

    [RelayCommand]
    private Task RecheckAsync() => _monitor.PollAsync();

    partial void OnSelectedRowChanged(AlertRowViewModel? value)
    {
        HasSelection = value is not null;
        if (value is null) return;
        var a = value.Source;
        SymbolText = a.Symbol;
        SelectedKind = KindOptions.FirstOrDefault(k => k.Kind == a.Kind) ?? KindOptions.FirstOrDefault();
        ThresholdText = FormatThreshold(a);
        IncludeExtendedHours = a.IncludeExtendedHours;
        SelectedRepeat = RepeatOptions.FirstOrDefault(r => r.Mode == a.Repeat) ?? RepeatOptions.FirstOrDefault();
        CooldownText = a.CooldownMinutes.ToString();
        NoteText = a.Note;
    }

    private void Reload()
    {
        _suppressToggle = true;
        try
        {
            Rows.Clear();
            foreach (var a in _store.Alerts.OrderBy(a => a.Symbol).ThenBy(a => a.Kind))
            {
                var row = new AlertRowViewModel(a);
                ApplyRow(row, a);
                Rows.Add(row);
            }
            History.Clear();
            foreach (var h in _store.History)
                History.Add(ToHistory(h));
            IsEmpty = Rows.Count == 0;
            HistoryEmpty = History.Count == 0;
            if (string.IsNullOrEmpty(StatusText) || StatusText.StartsWith(_loc["Al_Monitor"].Replace("{0}", ""), StringComparison.Ordinal))
                StatusText = string.Format(_loc["Al_Monitor"], _store.EnabledSymbols.Concat(_store.IvSymbols).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally { _suppressToggle = false; }
    }

    private void ApplyRow(AlertRowViewModel row, PriceAlert a)
    {
        row.Source = a;
        row.Symbol = a.Symbol;
        row.ConditionText = KindTitle(a.Kind);
        row.ThresholdText = FormatThreshold(a);
        row.RepeatText = a.Repeat == AlertRepeatMode.Once ? _loc["Al_Repeat_Once"] : _loc["Al_Repeat_Repeat"];
        row.LastFiredText = a.LastFiredAt is DateTimeOffset t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : _loc["Al_Never"];
        row.Note = a.Note;
        row.Enabled = a.Enabled;
    }

    private HistoryRowViewModel ToHistory(AlertHistoryEntry h) => new()
    {
        Symbol = h.Symbol,
        ConditionText = KindTitle(h.Kind) + " " + AlertText.FormatValue(h.Kind, h.Threshold)
            + (h.UsedExtended ? " " + _loc["Al_UsedExtended"] : ""),
        ValueText = AlertText.FormatValue(h.Kind, h.Value),
        TimeText = h.FiredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        SourceText = h.Source,
    };

    private string KindTitle(AlertKind kind) =>
        KindOptions.FirstOrDefault(k => k.Kind == kind)?.Title ?? kind.ToString();

    private static string FormatThreshold(PriceAlert a) => AlertText.FormatValue(a.Kind, a.Threshold);

    private void RebuildOptions()
    {
        KindOptions.Clear();
        foreach (var kind in new[] { AlertKind.PriceAbove, AlertKind.PriceBelow, AlertKind.PercentUp, AlertKind.PercentDown, AlertKind.VolumeAbove,
                     AlertKind.IvAbove, AlertKind.IvBelow })
            KindOptions.Add(new KindOption { Kind = kind, Title = _loc[$"Al_Kind_{kind}"] });
        RepeatOptions.Clear();
        RepeatOptions.Add(new RepeatOption { Mode = AlertRepeatMode.Once, Title = _loc["Al_Repeat_Once"] });
        RepeatOptions.Add(new RepeatOption { Mode = AlertRepeatMode.Repeat, Title = _loc["Al_Repeat_Repeat"] });
        SelectedKind ??= KindOptions.FirstOrDefault();
        SelectedRepeat ??= RepeatOptions.FirstOrDefault();
    }

    private void RebuildWatchSymbols()
    {
        WatchSymbols.Clear();
        foreach (var s in _watchlist.AllSymbols) WatchSymbols.Add(s);
        if (string.IsNullOrWhiteSpace(SymbolText) && WatchSymbols.Count > 0)
            SymbolText = WatchSymbols[0];
    }

    private bool TryReadDraft(out PriceAlert alert, out string error)
    {
        alert = new PriceAlert();
        if (!WatchlistStore.TryNormalizeSymbol(SymbolText, out var symbol, out _))
        {
            error = _loc["Al_Err_Symbol"];
            return false;
        }
        if (!double.TryParse(ThresholdText.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            && !double.TryParse(ThresholdText, NumberStyles.Float, CultureInfo.CurrentCulture, out threshold))
        {
            error = _loc["Al_Err_Threshold"];
            return false;
        }
        if (threshold <= 0 || ((SelectedKind?.Kind.IsIv() ?? false) && threshold > 1000))
        {
            error = _loc["Al_Err_Threshold"];
            return false;
        }
        if (!int.TryParse(CooldownText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var cooldown))
            cooldown = AlertEvaluator.DefaultCooldownMinutes;
        alert.Symbol = symbol;
        alert.Kind = SelectedKind?.Kind ?? AlertKind.PriceAbove;
        alert.Threshold = threshold;
        alert.IncludeExtendedHours = IncludeExtendedHours;
        alert.Repeat = SelectedRepeat?.Mode ?? AlertRepeatMode.Once;
        alert.CooldownMinutes = cooldown;
        alert.Note = (NoteText ?? "").Trim();
        error = "";
        return true;
    }
}