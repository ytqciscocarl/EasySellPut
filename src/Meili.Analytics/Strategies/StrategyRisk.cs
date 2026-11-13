namespace Meili.Analytics;

/// <summary>B7: Reg-T style initial margin estimate; NaN means unsupported. <see cref="DefinedRisk"/> means it equals the bounded maximum loss.</summary>
public sealed record MarginEstimate(double Amount, bool DefinedRisk);

/// <summary>B7: probability of profit and a rough margin figure for a strategy.</summary>
public static class StrategyRisk
{
    /// <summary>Reg-T: a naked short option needs 20 % of the underlying less the out-of-the-money amount (at least 10 %), plus premium.</summary>
    public const double NakedRate = 0.20, NakedFloorRate = 0.10, StockRate = 0.50;

    /// <summary>
    /// Probability that the strategy shows a profit at its earliest expiry when the underlying is lognormal with
    /// risk-neutral drift r − q and volatility <paramref name="volatility"/>. With a single expiry the profit region comes
    /// from the exact expiry breakevens; with mixed expiries later legs are valued by Black-Scholes at the earliest expiry.
    /// Returns NaN when the strategy has no option leg or the earliest expiry has passed.
    /// </summary>
    public static double ProbabilityOfProfit(OptionStrategy strategy, double spot, double volatility, StrategyValuation now)
    {
        if (strategy.EarliestExpiry is not { } first) return double.NaN;
        double t = now.YearsTo(first);
        if (!(t > 0) || !(spot > 0) || !(volatility > 0)) return double.NaN;

        double sd = volatility * Math.Sqrt(t);
        double mu = Math.Log(spot) + (now.Rate - now.DividendYield - 0.5 * volatility * volatility) * t;
        double Cdf(double x) => x <= 0 ? 0 : x == double.PositiveInfinity ? 1 : NormalDistribution.Cdf((Math.Log(x) - mu) / sd);

        Func<double, double> pnl;
        IReadOnlyList<double> breakevens;
        if (!strategy.HasMixedExpiries)
        {
            pnl = strategy.PnlAtExpiry;
            breakevens = strategy.AnalyzeAtExpiry().Breakevens;
        }
        else
        {
            var atFirst = StrategyValuation.AtDate(first, now.Rate, now.DividendYield) with { DefaultVolatility = now.DefaultVolatility ?? volatility };
            pnl = s => strategy.PnlAt(s, atFirst);
            breakevens = strategy.AnalyzeAt(atFirst, spot * Math.Exp(-6 * sd), spot * Math.Exp(6 * sd)).Breakevens;
        }

        var edges = new List<double>(breakevens.Count + 2) { 0 };
        edges.AddRange(breakevens.Where(b => b > 0).Order());
        edges.Add(double.PositiveInfinity);
        double p = 0;
        for (int i = 0; i + 1 < edges.Count; i++)
        {
            double a = edges[i], b = edges[i + 1];
            double probe = double.IsPositiveInfinity(b) ? Math.Max(a, spot) * 2 + 1 : a == 0 ? b / 2 : (a + b) / 2;
            if (pnl(probe) > 0) p += Cdf(b) - Cdf(a);
        }
        return Math.Clamp(p, 0, 1);
    }

    /// <summary>
    /// Rough Reg-T initial requirement. An option-only strategy whose short options are all matched by long ones of the same type, with bounded loss, needs its maximum loss (debit paid, or spread
    /// width less credit). Otherwise legs are summed: long options pay premium, stock 50 % of value, short calls covered by long
    /// stock need nothing extra, other short options the naked formula. Broker house rules (often higher) are not modelled.
    /// </summary>
    public static MarginEstimate RegTMargin(OptionStrategy strategy, double spot)
    {
        if (strategy.HasMixedExpiries) return new MarginEstimate(double.NaN, false);
        bool hasStock = strategy.Legs.Any(l => !l.IsOption);
        bool shortsHedged = Hedged(strategy, LegType.Call) && Hedged(strategy, LegType.Put);
        if (!hasStock && shortsHedged)
        {
            var a = strategy.AnalyzeAtExpiry();
            if (!a.IsMaxLossUnbounded) return new MarginEstimate(Math.Max(0, -a.MaxLoss), true);
        }

        double total = 0;
        double coverShares = strategy.Legs.Where(l => !l.IsOption && l.Side == PositionSide.Long).Sum(l => (double)l.Quantity);
        foreach (var leg in strategy.Legs)
        {
            double units = Math.Abs(leg.Units);
            if (!leg.IsOption) { total += StockRate * units * leg.Premium; continue; }
            if (leg.Side == PositionSide.Long) { total += units * leg.Premium; continue; }
            if (leg.Type == LegType.Call && coverShares > 0)
            {
                double covered = Math.Min(coverShares, units);
                coverShares -= covered;
                units -= covered;
                if (units <= 0) continue;
            }
            double otm = leg.Type == LegType.Call ? Math.Max(0, leg.Strike - spot) : Math.Max(0, spot - leg.Strike);
            double floor = NakedFloorRate * (leg.Type == LegType.Call ? spot : leg.Strike);
            total += units * (Math.Max(NakedRate * spot - otm, floor) + leg.Premium);
        }
        return new MarginEstimate(total, false);
    }

    // Each short option of a type is matched by at least as many long units of the same type (spreads, condors, butterflies).
    private static bool Hedged(OptionStrategy s, LegType type) =>
        s.Legs.Where(l => l.Type == type).Sum(l => l.Units) >= 0;

    /// <summary>Volatility for <see cref="ProbabilityOfProfit"/>: the |units|-weighted IV of the option legs, or <paramref name="fallback"/>.</summary>
    public static double RepresentativeVolatility(OptionStrategy strategy, double fallback)
    {
        double w = 0, sum = 0;
        foreach (var l in strategy.Legs)
        {
            if (!l.IsOption || !l.HasImpliedVolatility || l.ImpliedVolatility <= 0) continue;
            w += Math.Abs(l.Units);
            sum += Math.Abs(l.Units) * l.ImpliedVolatility;
        }
        return w > 0 ? sum / w : fallback;
    }
}
