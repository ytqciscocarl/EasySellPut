using Meili.Automation;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Tests;

[Collection(nameof(UiPlatformCollection))]
public sealed class ChartEventTests : IDisposable
{
    private readonly string _path=Path.Combine(Path.GetTempPath(),"meili-events-"+Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Monday=new(2025,1,6,14,30,0,TimeSpan.Zero);
    private ChartsViewModel? _vm;
    private static MarketEvent Event(DateOnly? date,DateTimeOffset? published=null)=>new("event","AAPL","news","Title",published,date,null,published is null?"date":"timestamp","unverified","fixture",null,"https://example.com",null,Monday,null,null);
    private static MarketTimeline Timeline(params MarketEvent[] events)=>new("AAPL",events,[],"complete",Monday,false,0);
    [Fact]
    public void Weekend_events_use_next_session_while_original_dates_remain_unchanged()
    {
        var weekend=new DateOnly(2025,1,5);
        var series=new BarSeries{Symbol="AAPL",Source="fixture",Interval=BarInterval.Day1,Bars=[Bar.CloseOnly(Monday.AddDays(-3),100,1),Bar.CloseOnly(Monday,101,1)]};
        var result=Assert.Single(ChartEventAlignment.Map(Timeline(Event(weekend)),series,ChartRangeKind.Day1));
        Assert.Equal(Monday.ToUnixTimeSeconds(),result.Time);Assert.Equal("next_bar",result.Alignment);Assert.Equal(weekend,result.Event.Date);
    }
    [Fact]
    public void Intraday_after_hours_never_appears_on_an_earlier_bar_and_undated_future_events_stay_in_list()
    {
        var series=new BarSeries{Symbol="AAPL",Source="fixture",Interval=BarInterval.Minute5,Bars=[Bar.CloseOnly(Monday,100,1),Bar.CloseOnly(Monday.AddMinutes(5),101,1)]};
        var result=ChartEventAlignment.Map(Timeline(Event(new(2025,1,6),Monday.AddMinutes(1)),Event(new(2025,1,6),Monday.AddHours(8)),Event(new(2099,1,1)),Event(null)),series,ChartRangeKind.Minute5);
        Assert.Equal(Monday.AddMinutes(5).ToUnixTimeSeconds(),result[0].Time);
        Assert.All(result.Skip(1),e=>Assert.Null(e.Time));
        Assert.Empty(ChartEventAlignment.Map(Timeline(Event(new(2025,1,6))),series with{Symbol="MSFT"},ChartRangeKind.Minute5));
    }
    [Theory]
    [InlineData(ChartRangeKind.Week1)]
    [InlineData(ChartRangeKind.Month1)]
    public void Period_markers_keep_original_date_and_label_the_aggregation(ChartRangeKind range)
    {
        var series=new BarSeries{Symbol="AAPL",Source="fixture",Interval=BarInterval.Day1,Bars=[Bar.CloseOnly(Monday,100,1),Bar.CloseOnly(Monday.AddDays(7),101,1)]};
        var item=Assert.Single(ChartEventAlignment.Map(Timeline(Event(new(2025,1,8))),series,range));
        Assert.Equal(Monday.ToUnixTimeSeconds(),item.Time);Assert.Equal("period_bucket",item.Alignment);Assert.Equal(new DateOnly(2025,1,8),item.Event.Date);
    }
    [Fact]
    public async Task News_opens_only_by_current_event_identity_and_stale_symbol_is_rejected()
    {
        var ui=new FakeUiPlatform();Ui.Platform=ui;var loc=new LocalizationService();var settings=new SettingsStore(_path);
        var data=new PricingSnapshotTests.SnapshotData{Bars=new(){Symbol="AAPL",Source="fixture",Interval=BarInterval.Minute5,Bars=[Bar.CloseOnly(Monday,100,1)]},News=(_,_)=>Task.FromResult<IReadOnlyList<NewsItem>>([new(){Title="Actual article",Url="https://example.com/news",Source="fixture",Published=Monday}])};
        _vm=new(new WatchlistStore(settings),data,loc,new SymbolContext(),settings,new ChartDrawingStore(_path));_vm.SymbolText="AAPL";await _vm.LoadAsync();
        var id=Assert.Single(_vm.Timeline!.Events).Id;
        _vm.OpenTimelineEvent("arbitrary-url");Assert.Empty(ui.Articles);
        _vm.OpenTimelineEvent(id);Assert.Equal("https://example.com/news",Assert.Single(ui.Articles).Url);
        _vm.SymbolText="MSFT";_vm.OpenTimelineEvent(id);Assert.Single(ui.Articles);
    }
    [Fact]
    public async Task Synchronous_timeline_is_included_in_initial_fit_and_unchanged_refresh_does_not_redraw()
    {
        Ui.Platform=new FakeUiPlatform();var settings=new SettingsStore(_path);var loc=new LocalizationService();var requests=0;
        var data=new PricingSnapshotTests.SnapshotData{Bars=new(){Symbol="AAPL",Source="fixture",Interval=BarInterval.Minute5,Bars=[Bar.CloseOnly(Monday,100,1)]},News=(_,_)=>{requests++;return Task.FromResult<IReadOnlyList<NewsItem>>([]);}};
        _vm=new(new WatchlistStore(settings),data,loc,new SymbolContext(),settings,new ChartDrawingStore(_path));_vm.SymbolText="AAPL";
        var renders=new List<bool>();_vm.PlotChanged+=(_,_)=>renders.Add(_vm.PreserveView);
        await _vm.LoadAsync();Assert.Equal([false],renders);Assert.NotNull(_vm.Timeline);
        await _vm.LoadAsync(refreshOnly:true);Assert.Equal([false],renders);Assert.Equal(1,requests);
    }
    [Fact]
    public async Task A_late_provider_response_cannot_replace_the_new_symbol_timeline()
    {
        Ui.Platform=new FakeUiPlatform();var settings=new SettingsStore(_path);var loc=new LocalizationService();
        var first=new TaskCompletionSource<IReadOnlyList<NewsItem>>();
        var data=new PricingSnapshotTests.SnapshotData{Bars=new(){Symbol="AAPL",Source="fixture",Interval=BarInterval.Minute5,Bars=[Bar.CloseOnly(Monday,100,1)]},News=(s,_)=>s=="AAPL"?first.Task:Task.FromResult<IReadOnlyList<NewsItem>>([])};
        _vm=new(new WatchlistStore(settings),data,loc,new SymbolContext(),settings,new ChartDrawingStore(_path));_vm.SymbolText="AAPL";await _vm.LoadAsync();
        data.Bars=data.Bars with{Symbol="MSFT"};_vm.SymbolText="MSFT";await _vm.LoadAsync();
        first.SetResult([new(){Title="Old",Url="https://example.com",Published=Monday}]);await Task.Delay(30);
        Assert.Equal("MSFT",_vm.Timeline!.Symbol);Assert.Empty(_vm.Timeline.Events);
    }
    [Fact]
    public async Task External_drawing_change_is_reported_in_the_desktop_without_overwriting()
    {
        Ui.Platform=new FakeUiPlatform();var settings=new SettingsStore(_path);var loc=new LocalizationService();
        var data=new PricingSnapshotTests.SnapshotData{Bars=new(){Symbol="AAPL",Source="fixture",Interval=BarInterval.Minute5,Bars=[Bar.CloseOnly(Monday,100,1)]}};
        _vm=new(new WatchlistStore(settings),data,loc,new SymbolContext(),settings,new ChartDrawingStore(_path));_vm.SymbolText="AAPL";await _vm.LoadAsync();
        new ChartDrawingStore(_path).Add("AAPL",new(DrawingKind.HorizontalLine,150));
        _vm.StartDrawingCommand.Execute(DrawingKind.HorizontalLine);
        Assert.False(_vm.AddDrawingPoint(Monday,100));Assert.Equal(loc["Ch_DrawingConflict"],_vm.StatusText);
        Assert.Equal(150,Assert.Single(new ChartDrawingStore(_path).For("AAPL")).Price1);
    }
    public void Dispose(){_vm?.Deactivate();if(Directory.Exists(_path))Directory.Delete(_path,true);}
}
