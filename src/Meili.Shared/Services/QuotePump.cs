using Meili.Core.Diagnostics;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Core.Time;
namespace Meili.Shared.Services;

/// <summary>The only quote timer. No subscribers means no wakeups; passive background quotes use 30s, alerts keep 5/10/60s.</summary>
public sealed class QuotePump : IDisposable
{
    private readonly QuoteHub _hub;private readonly IUiTimer _timer=Ui.Timer();
    private readonly bool _alertCadence;private readonly DesktopActivity? _activity;private readonly AlertStore? _alerts;
    private readonly CancellationTokenSource _lifetime=new();private bool _started,_disposed;private int _busy;
    public QuotePump(QuoteHub hub,DesktopActivity? activity=null,AlertStore? alerts=null,AppLaunchOptions? options=null)
    {
        _hub=hub;_alertCadence=options?.NoAlerts!=true;_activity=activity;_alerts=alerts;_timer.Tick+=Tick;
        hub.SubscriptionsChanged+=Schedule;if(activity is not null)activity.Changed+=Schedule;if(alerts is not null)alerts.Changed+=Schedule;
    }
    public TimeSpan CurrentInterval=>_timer.Interval;
    public bool IsPolling=>_timer.IsEnabled;
    public void Start(){if(_disposed)return;_started=true;RefreshSchedule();}
    public void Stop(){_started=false;_timer.Stop();}
    private void Schedule(object? sender,EventArgs e)=>Ui.Platform.Run(RefreshSchedule);
    private void RefreshSchedule()
    {
        if(_disposed)return;
        if(!_started||_hub.SubscribedSymbols.Count==0){_timer.Stop();return;}
        var interval=DesktopActivity.PollInterval(DateTimeOffset.Now,_activity?.AnyVisible??true,_alertCadence&&_alerts?.EnabledSymbols.Count>0);
        if(_timer.Interval!=interval)_timer.Interval=interval;if(!_timer.IsEnabled)_timer.Start();
    }
    private async void Tick(object? sender,EventArgs e)
    {
        if(!_started||Interlocked.Exchange(ref _busy,1)==1)return;
        try{await _hub.PollAsync(_lifetime.Token);}
        catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
        catch(Exception ex){AppLog.Warn("quotes","poll failed: "+ex.Message);}
        finally{Interlocked.Exchange(ref _busy,0);RefreshSchedule();}
    }
    public void Dispose(){if(_disposed)return;Stop();_disposed=true;_lifetime.Cancel();_timer.Tick-=Tick;_hub.SubscriptionsChanged-=Schedule;if(_activity is not null)_activity.Changed-=Schedule;if(_alerts is not null)_alerts.Changed-=Schedule;_lifetime.Dispose();}
}
