using Meili.Core.Time;
namespace Meili.Shared.Services;

/// <summary>Presentation demand only. Alerts retain their original market-session cadence.</summary>
public sealed class DesktopActivity
{
    private readonly HashSet<string> _visible=[];
    public bool AnyVisible=>_visible.Count>0;
    public event EventHandler? Changed;
    public void SetVisible(string owner,bool visible){var changed=visible?_visible.Add(owner):_visible.Remove(owner);if(changed)Changed?.Invoke(this,EventArgs.Empty);}
    public static TimeSpan PollInterval(DateTimeOffset now,bool visible,bool quoteAlerts)
    {
        var normal=MarketClock.QuoteRefreshInterval(now);
        return !visible&&!quoteAlerts&&normal<TimeSpan.FromSeconds(30)?TimeSpan.FromSeconds(30):normal;
    }
}
