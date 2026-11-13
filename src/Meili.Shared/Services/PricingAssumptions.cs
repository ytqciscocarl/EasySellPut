using Meili.Analytics;
using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Core.Pricing;

namespace Meili.Shared.Services;

/// <summary>H1: the risk-free rate and dividend yield one symbol is priced with.</summary>
public sealed record PricingInputs(RiskFreeRate Rate, double DividendYield)
{
    public OptionEnrichmentSettings Enrichment(DateTimeOffset now, OptionChain? chain = null) =>
        new() { RiskFreeRate = Rate.Rate, DividendYield = DividendYield, Now = chain is null ? now : DataFreshness.SnapshotTime(chain, now) };
}

/// <summary>
/// H1 (with F5): one place for the pricing assumptions the options and strategy pages share — the rate from
/// <see cref="RiskFreeRateProvider"/> and the symbol's dividend yield (0 when no source has it).
/// </summary>
public sealed class PricingAssumptions(IMarketDataService data, RiskFreeRateProvider rates)
{
    /// <summary>Before the first load: the cached or configured rate, no dividend.</summary>
    public PricingInputs Initial => new(rates.Current, 0);

    public async Task<PricingInputs> ForAsync(string symbol, string area, CancellationToken ct)
    {
        var yieldTask = SafeData.DividendYieldAsync(data, symbol, area, ct);
        var rate = await rates.GetAsync(ct);
        return new PricingInputs(rate, await yieldTask);
    }
}
