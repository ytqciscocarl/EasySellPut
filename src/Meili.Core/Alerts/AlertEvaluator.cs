using Meili.Core.Models;

namespace Meili.Core.Alerts;

/// <summary>
/// Pure alert evaluation: last-price / change-percent / volume crosses, one-shot vs repeat+cooldown,
/// and pre/post last price when requested and present. Does not mutate the input alert.
/// </summary>
public static class AlertEvaluator
{
    public const int MinCooldownMinutes = 1;
    public const int MaxCooldownMinutes = 1440;
    public const int DefaultCooldownMinutes = 15;

    /// <summary>
    /// Last price used for price alerts. Whenever the market is not in its regular session, the extended last is used
    /// when <paramref name="includeExtended"/> is true and the quote actually has one (it is the most recent trade, also
    /// once the state turns <see cref="MarketState.Closed"/>); otherwise the regular last.
    /// If the regular last is missing, a positive extended last is used as a fallback when extended hours are included.
    /// </summary>
    public static double? EffectiveLastPrice(Quote quote, bool includeExtended, out bool usedExtended)
    {
        usedExtended = false;
        if (quote is null) return null;

        var extOk = quote.ExtendedPrice is double ext && ext > 0;
        var outsideRegular = quote.MarketState != MarketState.Regular;
        if (includeExtended && outsideRegular && extOk)
        {
            usedExtended = true;
            return quote.ExtendedPrice;
        }
        if (quote.Price > 0) return quote.Price;
        if (includeExtended && extOk)
        {
            usedExtended = true;
            return quote.ExtendedPrice;
        }
        return null;
    }

    public static double? EffectiveChangePercent(Quote quote, bool includeExtended, out bool usedExtended)
    {
        usedExtended = false;
        if (quote is null) return null;
        var outsideRegular = quote.MarketState != MarketState.Regular;
        if (includeExtended && outsideRegular && quote.ExtendedChangePercent is double ext)
        {
            usedExtended = true;
            return ext;
        }
        return quote.ChangePercent;
    }

    public static AlertEvalResult Evaluate(PriceAlert alert, Quote quote, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var next = alert.Clone();
        if (!alert.Enabled)
            return new AlertEvalResult(AlertEvalStatus.Disabled, false, null, false, next);

        var usedExtended = false;
        double? value = alert.Kind switch
        {
            AlertKind.PriceAbove or AlertKind.PriceBelow => EffectiveLastPrice(quote, alert.IncludeExtendedHours, out usedExtended),
            AlertKind.PercentUp or AlertKind.PercentDown => EffectiveChangePercent(quote, alert.IncludeExtendedHours, out usedExtended),
            AlertKind.VolumeAbove => quote?.Volume,
            _ => null, // IV kinds are evaluated with EvaluateIv
        };

        // Cross-source guard: after an extended-hours sample, a sample outside the regular session that only has the older
        // regular close (session end on a source without extended data, or a failover) is not comparable. Skip it and
        // keep the stored state, so it never looks like a cross. In the regular session evaluation is normal.
        if (alert.Kind is AlertKind.PriceAbove or AlertKind.PriceBelow or AlertKind.PercentUp or AlertKind.PercentDown
            && alert.IncludeExtendedHours && alert.HasSeenQuote && alert.LastSeenExtended
            && !usedExtended && quote is not null && quote.MarketState != MarketState.Regular)
            return new AlertEvalResult(AlertEvalStatus.InvalidQuote, false, value, usedExtended, next);

        return EvaluateValue(alert, next, value, usedExtended, now);
    }

    /// <summary>
    /// B1: IV alert against one sample. When the sample's basis differs from the previous one (IV30 ↔ ATM fallback),
    /// it is treated as a new first sample: the two measures differ by several points, which must not look like a cross.
    /// </summary>
    public static AlertEvalResult EvaluateIv(PriceAlert alert, IvSample? sample, DateTimeOffset now)
    {
        var next = alert.Clone();
        if (!alert.Enabled)
            return new AlertEvalResult(AlertEvalStatus.Disabled, false, null, false, next);
        if (!alert.Kind.IsIv() || sample is null || !double.IsFinite(sample.Iv) || sample.Iv <= 0)
            return new AlertEvalResult(AlertEvalStatus.InvalidQuote, false, null, false, next);
        if (alert.HasSeenQuote && alert.LastSeenIvBasis != sample.Basis)
        {
            next.LastSeenIv = sample.Percent;
            next.LastSeenIvBasis = sample.Basis;
            return new AlertEvalResult(AlertEvalStatus.WaitingForSample, false, sample.Percent, false, next);
        }
        next.LastSeenIvBasis = sample.Basis;
        return EvaluateValue(alert, next, sample.Percent, false, now);
    }

    private static AlertEvalResult EvaluateValue(PriceAlert alert, PriceAlert next, double? value, bool usedExtended, DateTimeOffset now)
    {
        if (value is null ||
            (alert.Kind is AlertKind.PriceAbove or AlertKind.PriceBelow && value <= 0) ||
            (alert.Kind is AlertKind.VolumeAbove && value < 0) ||
            (alert.Kind.IsIv() && value <= 0))
            return new AlertEvalResult(AlertEvalStatus.InvalidQuote, false, value, usedExtended, next);

        RecordSeen(next, alert.Kind, value.Value, usedExtended);

        if (!alert.HasSeenQuote)
        {
            next.HasSeenQuote = true;
            return new AlertEvalResult(AlertEvalStatus.WaitingForSample, false, value, usedExtended, next);
        }

        var prev = Previous(alert);
        var crossed = IsCross(alert.Kind, alert.Threshold, prev, value.Value);
        if (!crossed)
            return new AlertEvalResult(AlertEvalStatus.Armed, false, value, usedExtended, next);

        if (alert.Repeat == AlertRepeatMode.Repeat && alert.LastFiredAt is DateTimeOffset last)
        {
            var cooldown = TimeSpan.FromMinutes(Math.Clamp(alert.CooldownMinutes, MinCooldownMinutes, MaxCooldownMinutes));
            if (now - last < cooldown)
                return new AlertEvalResult(AlertEvalStatus.Cooling, false, value, usedExtended, next);
        }

        next.LastFiredAt = now;
        if (alert.Repeat == AlertRepeatMode.Once)
            next.Enabled = false;
        return new AlertEvalResult(AlertEvalStatus.Fired, true, value, usedExtended, next);
    }

    public static AlertHistoryEntry HistoryFrom(PriceAlert alert, AlertEvalResult result, Quote quote, DateTimeOffset now) => new()
    {
        AlertId = alert.Id,
        Symbol = alert.Symbol,
        Kind = alert.Kind,
        Threshold = alert.Threshold,
        Value = result.Value ?? 0,
        FiredAt = now,
        Source = quote.Source ?? "",
        UsedExtended = result.UsedExtended,
    };

    public static AlertHistoryEntry HistoryFromIv(PriceAlert alert, AlertEvalResult result, IvSample sample, DateTimeOffset now) => new()
    {
        AlertId = alert.Id,
        Symbol = alert.Symbol,
        Kind = alert.Kind,
        Threshold = alert.Threshold,
        Value = result.Value ?? 0,
        FiredAt = now,
        Source = sample.Basis == IvBasis.Iv30 ? sample.Source + " IV30" : $"{sample.Source} ATM {sample.Expiration:yyyy-MM-dd}",
    };

    private static double? Previous(PriceAlert alert) => alert.Kind switch
    {
        AlertKind.IvAbove or AlertKind.IvBelow => alert.LastSeenIv,
        AlertKind.PriceAbove or AlertKind.PriceBelow => alert.LastSeenPrice,
        AlertKind.PercentUp or AlertKind.PercentDown => alert.LastSeenChangePercent,
        AlertKind.VolumeAbove => alert.LastSeenVolume,
        _ => null,
    };

    private static void RecordSeen(PriceAlert next, AlertKind kind, double value, bool usedExtended)
    {
        switch (kind)
        {
            case AlertKind.PriceAbove:
            case AlertKind.PriceBelow:
                next.LastSeenPrice = value;
                next.LastSeenExtended = usedExtended;
                break;
            case AlertKind.PercentUp:
            case AlertKind.PercentDown:
                next.LastSeenChangePercent = value;
                next.LastSeenExtended = usedExtended;
                break;
            case AlertKind.VolumeAbove:
                next.LastSeenVolume = value;
                break;
            case AlertKind.IvAbove:
            case AlertKind.IvBelow:
                next.LastSeenIv = value;
                break;
        }
    }

    /// <summary>True when the previous sample was on the untriggered side and the current sample has reached the trigger side.</summary>
    public static bool IsCross(AlertKind kind, double threshold, double? previous, double current) => kind switch
    {
        AlertKind.PriceAbove or AlertKind.PercentUp or AlertKind.VolumeAbove or AlertKind.IvAbove
            => previous is double p && p < threshold && current >= threshold,
        AlertKind.PriceBelow or AlertKind.IvBelow => previous is double p && p > threshold && current <= threshold,
        AlertKind.PercentDown => previous is double p && p > -threshold && current <= -threshold,
        _ => false,
    };
}