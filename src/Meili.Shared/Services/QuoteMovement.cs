using System.Globalization;
using Meili.Core.Models;

namespace Meili.Shared.Services;

/// <summary>Nullable presentation values: unavailable movement is never displayed as an unchanged market.</summary>
public static class QuoteMovement
{
    public static (double? Change,double? Percent) Values(Quote quote)
    {
        static double? Finite(double? value)=>value is { } v && double.IsFinite(v)?v:null;
        var change=Finite(quote.Change);
        var previous=Finite(quote.PreviousClose);
        if(change is null && previous is { } p && double.IsFinite(quote.Price))change=Finite(quote.Price-p);
        var percent=Finite(quote.ChangePercent);
        if(percent is null && previous is >0 && change is { } c)percent=Finite(c/previous.Value*100);
        return (change,percent);
    }
    public static string Percent(double? value)=>value is { } v && double.IsFinite(v)?v.ToString("+0.00;-0.00;0.00",CultureInfo.InvariantCulture)+"%":"—";
}
