using Meili.Core.Alerts;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Core.Time;

namespace Meili.Shared.Tests;

public class AlertSnapshotTests
{
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, 0)]
    [InlineData(false, 15)]
    public void Old_declared_quotes_do_not_notify_or_consume_a_one_shot_alert(bool realtime, int? delay)
    {
        var directory = Path.Combine(Path.GetTempPath(), "meili-alert-snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = MarketClock.FromEastern(new DateTime(2026, 11, 13, 11, 0, 0));
            var settings = new SettingsStore(directory);
            settings.Current.Alerts.MaxQuoteDelayMinutes = 20;
            var store = new AlertStore(directory);
            var rule = store.Upsert(new PriceAlert { Symbol = "TEST", Kind = AlertKind.PriceAbove, Threshold = 100 });
            var notifier = new RecordingNotifier();
            using var monitor = new AlertMonitor(store, new QuoteHub(new PricingSnapshotTests.SnapshotData()), notifier, new AppLaunchOptions(), settings);
            var fresh = new Quote
            {
                Symbol = "TEST", Price = 99, AsOf = now.AddMinutes(-1), FetchedAt = now,
                IsRealtime = realtime, DelayMinutes = delay, MarketState = MarketState.Regular
            };
            monitor.OnBatchCore(new QuoteBatch(["TEST"], [fresh], null, now), now);
            var old = fresh with { Price = 101, AsOf = now.AddHours(-1) };
            monitor.OnBatchCore(new QuoteBatch(["TEST"], [old], null, now), now);
            Assert.Equal(0, notifier.Notifications);
            Assert.Empty(store.History);
            Assert.True(store.Find(rule.Id)!.Enabled);
            Assert.Equal(99, store.Find(rule.Id)!.LastSeenPrice);
            Assert.Contains("TEST", monitor.LastCheck!.SkippedStale);

            monitor.OnBatchCore(new QuoteBatch(["TEST"], [fresh with { Price = 101 }], null, now), now);
            Assert.Equal(1, notifier.Notifications);
            Assert.Single(store.History);
            Assert.False(store.Find(rule.Id)!.Enabled);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class RecordingNotifier : IAlertNotifier
    {
        public int Notifications { get; private set; }
        public void ShowAlert(PriceAlert alert, AlertEvalResult result, Quote? quote) => Notifications++;
        public void ShowIvAlert(PriceAlert alert, AlertEvalResult result, IvSample sample) { }
        public void ShowMonitorInterrupted(MonitorHealth health) { }
        public void ShowMonitorRecovered() { }
    }
}
