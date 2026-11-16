using System.Text.Json;
using Meili.Core.Models;
using Meili.Data.Providers.Yahoo;

namespace Meili.Data.Tests;
public sealed class CalendarMetadataTests
{
    [Theory]
    [InlineData("true",EventDateStatus.Estimated)]
    [InlineData("false",EventDateStatus.SourceConfirmed)]
    [InlineData("null",EventDateStatus.Unknown)]
    public void Calendar_confirmation_requires_an_explicit_provider_flag(string flag,EventDateStatus expected)
    {
        using var json=JsonDocument.Parse("{\"quoteSummary\":{\"result\":[{\"calendarEvents\":{\"earnings\":{\"earningsDate\":[{\"raw\":1795132800},{\"raw\":1795219200}],\"isEarningsDateEstimate\":"+flag+"}}}]}}");
        var f=YahooProvider.ParseQuoteSummary(json.RootElement,"AAPL")!;
        Assert.Equal(expected,f.EarningsDateStatus);
        Assert.NotNull(f.NextEarningsDate);Assert.Equal(f.NextEarningsDate.Value.AddDays(1),f.EarningsDateEnd);
        Assert.Equal(EventDateStatus.Unknown,f.ExDividendDateStatus);
    }
}
