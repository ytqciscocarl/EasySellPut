using System.Collections.Concurrent;
using Meili.Core.Alerts;
using Meili.Core.Settings;
using Meili.Data.Cache;
using Microsoft.Data.Sqlite;
namespace Meili.Shared.Tests;
[Collection(nameof(UiPlatformCollection))]
public sealed class UpstreamIntegrationTests
{
    [Fact] public async Task Canceled_SQLite_continuation_cannot_apply_stale_IV_alert_state()
    {
        var previousUi=Ui.Platform;Ui.Platform=new FakeUiPlatform();var dir=Path.Combine(Path.GetTempPath(),"iv-upstream-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            var store=new AlertStore(dir);var rule=store.Upsert(new PriceAlert{Symbol="TEST",Kind=AlertKind.IvAbove,Threshold=30});var now=DateTimeOffset.UtcNow;
            var db=new MarketDb(Path.Combine(dir,"market.db"));using var sampler=new IvAlertSampler(store,new Reading(),new Quiet(),new SettingsStore(dir),db,new AppLaunchOptions());var updates=0;sampler.Updated+=(_,_)=>updates++;
            using var cts=new CancellationTokenSource();using var context=new PausedContext();
            using var blocker=new SqliteConnection($"Data Source={db.FilePath};Pooling=False");blocker.Open();using var command=blocker.CreateCommand();command.CommandText="BEGIN IMMEDIATE";command.ExecuteNonQuery();
            Task round;var originalContext=SynchronizationContext.Current;
            try{SynchronizationContext.SetSynchronizationContext(context);round=sampler.SampleAsync(now,cts.Token);}
            finally{SynchronizationContext.SetSynchronizationContext(originalContext);}
            command.CommandText="COMMIT";command.ExecuteNonQuery();
            // The worker has finished its SQLite write; hold its UI continuation until after cancellation.
            await context.WaitForPost();cts.Cancel();context.Drain();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>round);
            Assert.False(store.Find(rule.Id)!.HasSeenQuote);Assert.Null(sampler.LastRun);Assert.Equal(0,updates);
        }
        finally{Ui.Platform=previousUi;SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
    [Fact] public async Task Leaving_options_during_async_open_interest_does_not_publish_a_late_table()
    {
        var previousUi=Ui.Platform;Ui.Platform=new FakeUiPlatform();var dir=Path.Combine(Path.GetTempPath(),"oi-upstream-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            var settings=new SettingsStore(dir);var expiry=new DateOnly(2099,12,18);var now=DateTimeOffset.UtcNow;
            var data=new PricingSnapshotTests.SnapshotData{Chain=new Meili.Core.Models.OptionChain{Underlying="TEST",UnderlyingPrice=100,AsOf=now,Source="fixture",Expiration=expiry,Expirations=[expiry],Contracts=[new(){ContractSymbol="TESTCALL",Underlying="TEST",Right=Meili.Core.Models.OptionRight.Call,Strike=100,Expiration=expiry,Bid=4,Ask=5,Last=4.5,ImpliedVolatility=.4}]}};
            var rates=new Meili.Core.Pricing.RiskFreeRateProvider(data,()=>new PricingSettings{RateMode=RiskFreeRateMode.Fixed,FixedRatePercent=0});var db=new MarketDb(Path.Combine(dir,"market.db"));
            var vm=new Meili.Shared.ViewModels.OptionsViewModel(new WatchlistStore(settings),data,new LocalizationService(),new SymbolContext(),new PricingAssumptions(data,rates),settings,new VolatilityInsights(new Reading(),data,rates,db)){SymbolText="TEST"};
            using var context=new PausedContext();using var blocker=new SqliteConnection($"Data Source={db.FilePath};Pooling=False");blocker.Open();using var command=blocker.CreateCommand();command.CommandText="BEGIN IMMEDIATE";command.ExecuteNonQuery();
            Task load;var original=SynchronizationContext.Current;try{SynchronizationContext.SetSynchronizationContext(context);load=vm.LoadAsync();}finally{SynchronizationContext.SetSynchronizationContext(original);}
            command.CommandText="COMMIT";command.ExecuteNonQuery();await context.WaitForPost();vm.Deactivate();context.Drain();await load;
            Assert.Empty(vm.Rows);Assert.False(vm.HasRows);Assert.False(vm.IsBusy);
        }
        finally{Ui.Platform=previousUi;SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
    private sealed class Reading:IIvSource
    {
        public Task<IvSample?> GetAsync(string symbol,DateTimeOffset now,CancellationToken ct=default)=>Task.FromResult<IvSample?>(new(symbol,.4,IvBasis.Iv30,now,"fixture"));
    }
    private sealed class PausedContext:SynchronizationContext,IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback,object? State)> _queue=new();private readonly SemaphoreSlim _ready=new(0);
        public override void Post(SendOrPostCallback callback,object? state){_queue.Enqueue((callback,state));_ready.Release();}
        public async Task WaitForPost(){if(!await _ready.WaitAsync(TimeSpan.FromSeconds(10)))throw new TimeoutException("SQLite continuation did not reach the UI context");}
        public void Drain(){var previous=Current;SetSynchronizationContext(this);try{while(_queue.TryDequeue(out var work))work.Callback(work.State);}finally{SetSynchronizationContext(previous);}}
        public void Dispose()=>_ready.Dispose();
    }
    private sealed class Quiet:IAlertNotifier
    {
        public void ShowAlert(PriceAlert a,AlertEvalResult r,Meili.Core.Models.Quote? q){}
        public void ShowIvAlert(PriceAlert a,AlertEvalResult r,IvSample s){}public void ShowMonitorInterrupted(MonitorHealth h){}public void ShowMonitorRecovered(){}
    }
}
