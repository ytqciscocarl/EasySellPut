using Meili.Core.Alerts;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Core.Time;

namespace Meili.Core.Tests;

/// <summary>F3 (stale quotes never trigger) and F10 (interrupted monitoring is reported once).</summary>
public class AlertGuardTests
{
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, 0)]
    [InlineData(false, 15)]
    public void Declared_realtime_or_delay_does_not_override_an_old_market_timestamp(bool realtime, int? delay)
    {
        var quote = Q(rt: realtime, delay: delay, asOf: InSession.AddHours(-1)) with { FetchedAt = InSession };
        Assert.Equal(AlertQuoteUse.TooStale, AlertQuoteGate.Check(quote, InSession, 20).Use);
        Assert.Equal(StaleReason.SourceLagging, QuoteStaleness.Check(quote, InSession));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, 15)]
    public void Expected_declared_delay_and_closed_session_prices_are_not_marked_stale(bool realtime, int? delay)
    {
        var quote = Q(rt: realtime, delay: delay, asOf: InSession.AddMinutes(-15)) with { FetchedAt = InSession };
        Assert.Equal(AlertQuoteUse.Usable, AlertQuoteGate.Check(quote, InSession, 20).Use);
        Assert.Equal(StaleReason.None, QuoteStaleness.Check(quote, InSession));
        var closed = quote with { AsOf = Weekend.AddDays(-1), FetchedAt = Weekend };
        Assert.Equal(AlertQuoteUse.Usable, AlertQuoteGate.Check(closed, Weekend, 20).Use);
        Assert.Equal(StaleReason.None, QuoteStaleness.Check(closed, Weekend));
    }

    private static readonly DateTimeOffset InSession = MarketClock.FromEastern(new DateTime(2026, 11, 12, 11, 0, 0));
    private static readonly DateTimeOffset Weekend = MarketClock.FromEastern(new DateTime(2026, 11, 14, 11, 0, 0));

    private static Quote Q(bool rt = false, int? delay = null, DateTimeOffset? asOf = null) =>
        new() { Symbol = "AAPL", Price = 1, Source = "x", IsRealtime = rt, DelayMinutes = delay, AsOf = asOf };

    [Fact]
    public void Realtime_and_declared_delay_within_limit_are_usable()
    {
        Assert.Equal(AlertQuoteUse.Usable, AlertQuoteGate.Check(Q(rt: true), InSession, 20).Use);
        Assert.Equal(AlertQuoteUse.Usable, AlertQuoteGate.Check(Q(delay: 15), InSession, 20).Use);
        Assert.Equal(AlertQuoteUse.TooStale, AlertQuoteGate.Check(Q(delay: 15), InSession, 10).Use);
    }

    [Fact]
    public void Measured_lag_beyond_limit_is_stale_and_unknown_is_flagged()
    {
        Assert.Equal(AlertQuoteUse.TooStale, AlertQuoteGate.Check(Q(asOf: InSession.AddMinutes(-45)), InSession, 20).Use);
        Assert.Equal(AlertQuoteUse.Usable, AlertQuoteGate.Check(Q(asOf: InSession.AddMinutes(-2)), InSession, 20).Use);
        // Outside the session lag cannot be measured: evaluate, but the toast must say the delay is unknown.
        Assert.Equal(AlertQuoteUse.UsableDelayUnknown, AlertQuoteGate.Check(Q(asOf: Weekend.AddHours(-30)), Weekend, 20).Use);
        Assert.Equal(AlertQuoteUse.UsableDelayUnknown, AlertQuoteGate.Check(Q(), InSession, 20).Use);
    }

    [Fact]
    public void Health_reports_interruption_once_and_recovery()
    {
        var h = new MonitorHealth(3);
        var t = InSession;
        Assert.Equal(MonitorTransition.None, h.RecordFailure(t, "a"));
        Assert.Equal(MonitorTransition.None, h.RecordFailure(t.AddMinutes(1), "b"));
        Assert.Equal(MonitorTransition.Interrupted, h.RecordFailure(t.AddMinutes(2), "c"));
        Assert.Equal(MonitorTransition.None, h.RecordFailure(t.AddMinutes(3), "d"));
        Assert.True(h.Interrupted);
        Assert.Equal(t, h.FailingSince);
        Assert.Equal("d", h.LastError);
        Assert.Equal(MonitorTransition.Recovered, h.RecordSuccess(t.AddMinutes(4)));
        Assert.False(h.Interrupted);
        Assert.Null(h.FailingSince);
        Assert.Equal(MonitorTransition.None, h.RecordSuccess(t.AddMinutes(5)));
    }

    [Fact]
    public void Alert_settings_default_and_clamp()
    {
        var s = new AppSettings();
        Assert.Equal(20, s.Alerts.MaxQuoteDelayMinutes);
        Assert.Equal(3, s.Alerts.FailureThreshold);
        s.Alerts.MaxQuoteDelayMinutes = 0;
        s.Alerts.FailureThreshold = 99;
        s.Normalize();
        Assert.Equal(1, s.Alerts.MaxQuoteDelayMinutes);
        Assert.Equal(20, s.Alerts.FailureThreshold);
        s.Alerts = null!;
        Assert.NotNull(s.Normalize().Alerts);
    }
}
