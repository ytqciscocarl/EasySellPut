using Meili.Core.Models;

namespace Meili.Automation;

/// <summary>An observed analytical entry reference, never a promise of executable liquidity.</summary>
public sealed record OptionEntryReference(double? Price, string Basis)
{
    public bool IsExecutable => false;
    public static OptionEntryReference From(OptionContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Bid is { } bid && contract.Ask is { } ask && double.IsFinite(bid) && double.IsFinite(ask) && bid > 0 && ask >= bid)
            return new(bid / 2 + ask / 2, "mid");
        if (contract.Last is { } last && double.IsFinite(last) && last > 0) return new(last, "last");
        return new(null, "missing");
    }
}
