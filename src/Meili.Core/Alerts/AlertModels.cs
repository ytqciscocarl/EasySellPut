namespace Meili.Core.Alerts;

/// <summary>How a price alert is compared against a live quote.</summary>
public enum AlertKind
{
    /// <summary>Last price crosses up through the threshold.</summary>
    PriceAbove,
    /// <summary>Last price crosses down through the threshold.</summary>
    PriceBelow,
    /// <summary>Session (or extended) change-percent crosses up through the threshold, e.g. +5.</summary>
    PercentUp,
    /// <summary>Change-percent crosses down through the negative of the threshold, e.g. threshold 5 means -5%.</summary>
    PercentDown,
    /// <summary>Session volume crosses up through the threshold.</summary>
    VolumeAbove,
    /// <summary>B1: implied volatility crosses up through the threshold, in percent (45 = 45%).</summary>
    IvAbove,
    /// <summary>B1: implied volatility crosses down through the threshold, in percent.</summary>
    IvBelow,
}

public static class AlertKindExtensions
{
    /// <summary>IV alerts are sampled every few minutes by the IV sampler, not from the quote stream.</summary>
    public static bool IsIv(this AlertKind kind) => kind is AlertKind.IvAbove or AlertKind.IvBelow;
}

/// <summary>Once: disable after the first fire. Repeat: re-arm after a re-cross and cooldown.</summary>
public enum AlertRepeatMode { Once, Repeat }

public enum AlertEvalStatus
{
    Disabled,
    InvalidQuote,
    WaitingForSample,
    Armed,
    Cooling,
    Fired,
}

/// <summary>One user-defined alert, persisted in alerts.json.</summary>
public sealed class PriceAlert
{
    public string Id { get; set; } = NewId();
    public string Symbol { get; set; } = "";
    public AlertKind Kind { get; set; } = AlertKind.PriceAbove;
    public double Threshold { get; set; }
    public bool Enabled { get; set; } = true;
    public AlertRepeatMode Repeat { get; set; } = AlertRepeatMode.Once;
    /// <summary>Minimum minutes between two fires of a repeating alert. Clamped to 1–1440.</summary>
    public int CooldownMinutes { get; set; } = 15;
    /// <summary>When true, pre-market / after-hours last price and extended change-percent are used if the quote has them.</summary>
    public bool IncludeExtendedHours { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastFiredAt { get; set; }
    public double? LastSeenPrice { get; set; }
    public double? LastSeenChangePercent { get; set; }
    public double? LastSeenVolume { get; set; }
    /// <summary>B1: last IV sample in percent, and what it measured (<see cref="IvBasis"/>), so a change of basis never counts as a cross.</summary>
    public double? LastSeenIv { get; set; }
    public IvBasis? LastSeenIvBasis { get; set; }
    /// <summary>False until the first valid quote is recorded. The first sample never fires, so an already-crossed level does not notify immediately.</summary>
    public bool HasSeenQuote { get; set; }
    /// <summary>
    /// True when the last recorded price or percent sample came from the extended-hours fields. Lets the evaluator refuse a
    /// sample that falls back to the older regular close outside regular hours (session end, or a failover to a source
    /// without extended data), which would otherwise look like a cross.
    /// </summary>
    public bool LastSeenExtended { get; set; }
    public string Note { get; set; } = "";

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public PriceAlert Clone() => new()
    {
        Id = Id,
        Symbol = Symbol,
        Kind = Kind,
        Threshold = Threshold,
        Enabled = Enabled,
        Repeat = Repeat,
        CooldownMinutes = CooldownMinutes,
        IncludeExtendedHours = IncludeExtendedHours,
        CreatedAt = CreatedAt,
        LastFiredAt = LastFiredAt,
        LastSeenPrice = LastSeenPrice,
        LastSeenChangePercent = LastSeenChangePercent,
        LastSeenVolume = LastSeenVolume,
        LastSeenIv = LastSeenIv,
        LastSeenIvBasis = LastSeenIvBasis,
        HasSeenQuote = HasSeenQuote,
        LastSeenExtended = LastSeenExtended,
        Note = Note,
    };
}

public sealed class AlertHistoryEntry
{
    public string AlertId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public AlertKind Kind { get; set; }
    public double Threshold { get; set; }
    public double Value { get; set; }
    public DateTimeOffset FiredAt { get; set; }
    public string Source { get; set; } = "";
    public bool UsedExtended { get; set; }
}

public sealed class AlertDocument
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public List<PriceAlert> Alerts { get; set; } = [];
    public List<AlertHistoryEntry> History { get; set; } = [];
}

/// <summary>Pure evaluation result. <see cref="Next"/> is a copy of the alert with updated state; the input is never mutated.</summary>
public sealed record AlertEvalResult(
    AlertEvalStatus Status,
    bool Fired,
    double? Value,
    bool UsedExtended,
    PriceAlert Next);