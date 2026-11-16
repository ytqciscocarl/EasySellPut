using Meili.Core.Models;
namespace Meili.Shared.Tests;
public sealed class QuoteMovementTests
{
    [Fact] public void Missing_changes_stay_missing_but_a_real_zero_stays_zero()
    {
        var missing=new Quote{Symbol="TEST",Price=100};Assert.Equal((null,null),QuoteMovement.Values(missing));Assert.Equal("—",QuoteMovement.Percent(null));
        var zero=missing with{Change=0,ChangePercent=0};Assert.Equal((0d,0d),QuoteMovement.Values(zero));Assert.Equal("0.00%",QuoteMovement.Percent(0));
    }
    [Fact] public void Derivation_requires_a_known_previous_close_and_rejects_nonfinite_values()
    {
        var q=new Quote{Symbol="TEST",Price=105,PreviousClose=100};Assert.Equal((5d,5d),QuoteMovement.Values(q));
        Assert.Null(QuoteMovement.Values(q with{PreviousClose=null,ChangePercent=double.NaN}).Percent);
        Assert.Equal("—",QuoteMovement.Percent(double.PositiveInfinity));
    }
}
