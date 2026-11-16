using Meili.Core.Alerts;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Core.Time;
using Meili.Data.Cache;
namespace Meili.Shared.Tests;
[Collection(nameof(UiPlatformCollection))]
public sealed class BackgroundPolicyTests
{
    [Fact] public void Passive_quotes_slow_down_but_alert_cadence_and_closed_market_cadence_do_not_change()
    {
        var regular=MarketClock.FromEastern(new DateTime(2026,11,16,10,0,0));var closed=MarketClock.FromEastern(new DateTime(2026,11,15,10,0,0));
        Assert.Equal(TimeSpan.FromSeconds(5),DesktopActivity.PollInterval(regular,true,false));Assert.Equal(TimeSpan.FromSeconds(30),DesktopActivity.PollInterval(regular,false,false));Assert.Equal(TimeSpan.FromSeconds(5),DesktopActivity.PollInterval(regular,false,true));Assert.Equal(TimeSpan.FromSeconds(60),DesktopActivity.PollInterval(closed,false,false));
    }
    [Fact] public async Task No_subscribers_means_no_timer_and_disposal_prevents_late_requests()
    {
        var ui=new FakeUiPlatform();Ui.Platform=ui;var hub=new QuoteHub(new PricingSnapshotTests.SnapshotData());var activity=new DesktopActivity();using var pump=new QuotePump(hub,activity);
        pump.Start();Assert.False(pump.IsPolling);hub.Subscribe("tray",["SPY"]);Assert.True(pump.IsPolling);ui.Timers.Single().Fire();await Task.Yield();Assert.Equal(1,hub.RequestCount);
        hub.Unsubscribe("tray");Assert.False(pump.IsPolling);pump.Dispose();ui.Timers.Single().Fire();await Task.Yield();Assert.Equal(1,hub.RequestCount);
    }
    [Fact] public void Background_start_is_explicit_and_smoke_keeps_its_exit_guards()
    {
        Assert.False(AppLaunchOptions.Parse([]).StartInBackground);var background=AppLaunchOptions.Parse(["--background"]);Assert.True(background.StartInBackground);Assert.False(background.NoAlerts);
        var smoke=AppLaunchOptions.Parse(["--background","--smoke-offline"]);Assert.True(smoke.StartInBackground&&smoke.Smoke&&smoke.ExitOnClose&&smoke.NoAlerts);
    }
    [Fact] public void Iv_scheduler_has_no_timer_without_enabled_iv_rules()
    {
        Ui.Platform=new FakeUiPlatform();var dir=Path.Combine(Path.GetTempPath(),"iv-idle-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            var store=new AlertStore(dir);var source=new EmptyIv();var db=new MarketDb(Path.Combine(dir,"market.db"));using var sampler=new IvAlertSampler(store,source,new QuietNotifier(),new SettingsStore(dir),db,new AppLaunchOptions());
            sampler.Start();Assert.False(sampler.IsScheduled);Assert.Equal(0,source.Calls);
            var rule=store.Upsert(new PriceAlert{Symbol="TEST",Kind=AlertKind.IvAbove,Threshold=40});Assert.True(sampler.IsScheduled);Assert.Equal(1,source.Calls);
            store.Remove(rule.Id);Assert.False(sampler.IsScheduled);sampler.Stop();store.Upsert(new PriceAlert{Symbol="TEST",Kind=AlertKind.IvAbove,Threshold=40});Assert.False(sampler.IsScheduled);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
    private sealed class EmptyIv:IIvSource{public int Calls;public Task<IvSample?> GetAsync(string symbol,DateTimeOffset now,CancellationToken ct=default){Calls++;return Task.FromResult<IvSample?>(null);}}
    private sealed class QuietNotifier:IAlertNotifier
    {
        public void ShowAlert(PriceAlert a,AlertEvalResult r,Quote? q){}public void ShowIvAlert(PriceAlert a,AlertEvalResult r,IvSample s){}
        public void ShowMonitorInterrupted(MonitorHealth h){}public void ShowMonitorRecovered(){}
    }
    [Fact] public void Visible_pinned_surface_keeps_demand_until_the_last_surface_hides()
    {
        var activity=new DesktopActivity();activity.SetVisible("main",true);activity.SetVisible("pinned",true);activity.SetVisible("main",false);Assert.True(activity.AnyVisible);activity.SetVisible("pinned",false);Assert.False(activity.AnyVisible);
    }
}
