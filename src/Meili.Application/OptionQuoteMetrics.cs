namespace Meili.Automation;

public static class OptionQuoteMetrics
{
    public static (double? Spread, double? Pct, bool Wide) SpreadOf(double? bid, double? ask)
    {
        if (bid is not > 0 || ask is not > 0 || !double.IsFinite(bid.Value) || !double.IsFinite(ask.Value) || ask < bid) return (null, null, false);
        var spread = ask.Value - bid.Value; var pct = spread / ((ask.Value + bid.Value) / 2);
        return (spread, pct, pct > .10 && spread > .05);
    }
}
