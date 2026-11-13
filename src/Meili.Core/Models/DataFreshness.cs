using Meili.Core.Time;

namespace Meili.Core.Models;

public enum FreshnessKind
{
    /// <summary>The source declares the data real-time.</summary>
    Realtime,
    /// <summary>The source declares a fixed delay (e.g. 15 minutes).</summary>
    Delayed,
    /// <summary>The source declares nothing, but its own timestamp lets us measure the lag during regular hours.</summary>
    Measured,
    /// <summary>Neither declared nor measurable.</summary>
    Unknown,
}

/// <summary>How old a piece of data is, and how we know.</summary>
/// <param name="Lag">Declared delay (Delayed), measured lag (Measured), zero (Realtime) or null (Unknown).</param>
public sealed record Freshness(FreshnessKind Kind, TimeSpan? Lag, DateTimeOffset? AsOf)
{
    /// <summary>Best estimate of the delay in minutes for threshold checks; null when unknown.</summary>
    public double? LagMinutes => Lag?.TotalMinutes;
}

/// <summary>
/// One place that decides what to tell the user about data timeliness. Rules (F1/F6):
/// only a source's own real-time flag makes data "real-time"; a declared delay is shown as such;
/// otherwise the lag is measured from the source's timestamp, but only while the regular session is open
/// (outside it, an old last-trade time is expected and says nothing about delay; inside it, a timestamp from
/// a previous session correctly shows up as a very large lag); everything else is "unknown".
/// </summary>
public static class DataFreshness
{
    public static DateTimeOffset SnapshotTime(SourcedData data, DateTimeOffset now)
    {
        var fetched = data.FetchedAt ?? now;
        var snapshot = data.AsOf ?? (data.DelayMinutes is > 0 and var delay ? fetched.AddMinutes(-delay) : fetched);
        return snapshot > now ? now : snapshot;
    }

    public static TimeSpan? MeasuredLag(SourcedData data, DateTimeOffset now)
    {
        if (data.AsOf is not { } asOf || MarketClock.GetSession(now) != MarketSession.Regular) return null;
        return now > asOf ? now - asOf : TimeSpan.Zero;
    }

    public static Freshness Evaluate(SourcedData data, DateTimeOffset now)
    {
        if (data.IsRealtime) return new Freshness(FreshnessKind.Realtime, TimeSpan.Zero, data.AsOf);
        if (data.DelayMinutes is > 0 and var d) return new Freshness(FreshnessKind.Delayed, TimeSpan.FromMinutes(d), data.AsOf);
        if (data.DelayMinutes == 0) return new Freshness(FreshnessKind.Realtime, TimeSpan.Zero, data.AsOf);
        if (MeasuredLag(data, now) is { } lag)
            return new Freshness(FreshnessKind.Measured, lag, data.AsOf);
        return new Freshness(FreshnessKind.Unknown, null, data.AsOf);
    }
}

public enum StaleReason
{
    None,
    /// <summary>We have not managed to refresh this quote for several refresh cycles (sources failing / offline).</summary>
    NotUpdated,
    /// <summary>Refreshed fine, but the source's own timestamp lags far behind during regular hours.</summary>
    SourceLagging,
}

/// <summary>
/// D8: when a displayed price should carry a "stale" marker. A declared delay is expected, but does not override
/// a market timestamp older than both that delay and the normal tolerance during the regular session.
/// </summary>
public static class QuoteStaleness
{
    /// <summary>Measured lag above this during the regular session counts as stale.</summary>
    public static readonly TimeSpan MaxMeasuredLag = TimeSpan.FromMinutes(20);

    /// <summary>A quote not refreshed for this long (three refresh cycles, at least two minutes) is stale.</summary>
    public static TimeSpan MaxAge(DateTimeOffset now)
    {
        var three = Time.MarketClock.QuoteRefreshInterval(now) * 3;
        return three < TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : three;
    }

    public static StaleReason Check(SourcedData data, DateTimeOffset now)
    {
        if (data.FetchedAt is { } fetched && now - fetched > MaxAge(now)) return StaleReason.NotUpdated;
        var allowedLag = TimeSpan.FromMinutes(Math.Max(MaxMeasuredLag.TotalMinutes, data.DelayMinutes ?? 0));
        if (DataFreshness.MeasuredLag(data, now) > allowedLag) return StaleReason.SourceLagging;
        return StaleReason.None;
    }
}
