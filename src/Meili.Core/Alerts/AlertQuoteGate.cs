using Meili.Core.Models;

namespace Meili.Core.Alerts;

public enum AlertQuoteUse
{
    /// <summary>Realtime, or delayed/lagging within the limit.</summary>
    Usable,
    /// <summary>The source states nothing about delay and no lag can be measured: evaluate, but say so in the toast.</summary>
    UsableDelayUnknown,
    /// <summary>Known to be older than the limit: do not evaluate (a stale cross would fire at the wrong time).</summary>
    TooStale,
}

/// <summary>F3: decides whether a quote is fresh enough to trigger alerts.</summary>
public static class AlertQuoteGate
{
    public static (AlertQuoteUse Use, Freshness Freshness) Check(Quote quote, DateTimeOffset now, int maxDelayMinutes)
    {
        var f = DataFreshness.Evaluate(quote, now);
        var limit = TimeSpan.FromMinutes(maxDelayMinutes);
        if (DataFreshness.MeasuredLag(quote, now) > limit) return (AlertQuoteUse.TooStale, f);
        var use = f.Kind switch
        {
            FreshnessKind.Realtime => AlertQuoteUse.Usable,
            FreshnessKind.Delayed or FreshnessKind.Measured => f.Lag is { } lag && lag > limit ? AlertQuoteUse.TooStale : AlertQuoteUse.Usable,
            _ => AlertQuoteUse.UsableDelayUnknown,
        };
        return (use, f);
    }
}
