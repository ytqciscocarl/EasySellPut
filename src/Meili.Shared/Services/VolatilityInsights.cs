using Meili.Analytics;
using Meili.Core.Abstractions;
using Meili.Core.Alerts;
using Meili.Core.Diagnostics;
using Meili.Core.Models;
using Meili.Core.Pricing;
using Meili.Core.Time;
using Meili.Data.Cache;
using CRight = Meili.Core.Models.OptionRight;

namespace Meili.Shared.Services;

/// <summary>B6: one point of the ATM IV term structure.</summary>
public sealed record TermPoint(DateOnly Expiration, int DaysToExpiry, double Iv);

/// <summary>B6: IV Rank from the locally recorded IV history (iv_samples, B1), the ATM term structure, and day-over-day open interest.</summary>
public sealed class VolatilityInsights(IIvSource iv, IMarketDataService data, RiskFreeRateProvider rates, MarketDb db)
{
    /// <summary>Expirations shown in the term structure (nearest first).</summary>
    public const int TermExpirations = 8;

    /// <summary>Takes a fresh IV reading, records it, and ranks it against the same kind of reading over the past year.</summary>
    public async Task<(IvSample Sample, IvRankResult? Rank, int Days)?> RankAsync(string symbol, DateTimeOffset now, CancellationToken ct)
    {
        if (await iv.GetAsync(symbol, now, ct) is not { } sample) return null;
        db.AddIvSample(symbol, sample.AsOf ?? now, sample.Basis.ToString(), sample.Iv, sample.Source);
        var kind = sample.Basis.ToString();
        var history = db.LoadIvSamples(symbol, now.AddDays(-370))
            .Where(s => s.Kind == kind)
            .Select(s => (DateOnly.FromDateTime(MarketClock.ToEastern(s.T).Date), s.Iv));
        var daily = IvRank.LastPerDay(history);
        var today = DateOnly.FromDateTime(MarketClock.ToEastern(now).Date);
        return (sample, IvRank.Compute(daily, today, sample.Iv), daily.Count(d => d.Day < today));
    }

    /// <summary>ATM IV of the nearest <see cref="TermExpirations"/> expirations.</summary>
    public async Task<IReadOnlyList<TermPoint>> TermStructureAsync(string symbol, IReadOnlyList<DateOnly> expirations, double spot, DateTimeOffset now, CancellationToken ct)
    {
        var rate = (await rates.GetAsync(ct)).Rate;
        var points = new List<TermPoint>();
        foreach (var exp in expirations.Where(e => OptionTime.DaysToExpiry(e, now) >= 1).Take(TermExpirations))
        {
            var chain = await data.GetChainAsync(symbol, exp, ct);
            var candidates = chain.Contracts.Where(c => c.Expiration == exp)
                .Select(c => new AtmCandidate(c.Right == CRight.Call ? Meili.Analytics.OptionRight.Call : Meili.Analytics.OptionRight.Put, c.Strike, c.ImpliedVolatility, c.Bid, c.Ask, c.Last))
                .ToList();
            var s = chain.UnderlyingPrice is > 0 ? chain.UnderlyingPrice.Value : spot;
            if (AtmVolatility.Estimate(candidates, s, exp, DataFreshness.SnapshotTime(chain, now), rate) is { } est)
                points.Add(new TermPoint(exp, OptionTime.DaysToExpiry(exp, now), est.Iv));
        }
        return points;
    }

    /// <summary>Stores today's open interest and returns the previous stored day's, by contract symbol.</summary>
    public IReadOnlyDictionary<string, double> SwapOpenInterest(string symbol, DateOnly day, IEnumerable<OptionContract> contracts)
    {
        var previous = db.LoadPreviousOpenInterest(symbol, day);
        db.SaveOpenInterest(symbol, day, contracts.Where(c => c.OpenInterest is >= 0).Select(c => (c.ContractSymbol, c.OpenInterest!.Value)));
        db.PruneOpenInterest(day.AddDays(-45));
        return previous;
    }
}
