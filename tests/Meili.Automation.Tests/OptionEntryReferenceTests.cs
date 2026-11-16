using Meili.Automation;
using Meili.Core.Models;

namespace Meili.Automation.Tests;

public class OptionEntryReferenceTests
{
    [Theory]
    [InlineData(2,4,9,3,"mid")]
    [InlineData(5,4,2,2,"last")]
    [InlineData(0,4,2,2,"last")]
    [InlineData(double.NaN,4,2,2,"last")]
    [InlineData(double.PositiveInfinity,4,2,2,"last")]
    public void Reference_prices_preserve_observations_and_never_promise_execution(double bid,double ask,double last,double price,string basis)
    {
        var c=new OptionContract{ContractSymbol="fixture",Underlying="AAPL",Right=OptionRight.Call,Strike=100,Expiration=new(2099,12,18),Bid=bid,Ask=ask,Last=last};
        var r=OptionEntryReference.From(c);
        Assert.Equal(price,r.Price);Assert.Equal(basis,r.Basis);Assert.False(r.IsExecutable);
        Assert.Null(OptionEntryReference.From(c with{Bid=null,Ask=null,Last=null,TheoreticalPrice=5}).Price);
    }
}
