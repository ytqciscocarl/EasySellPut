using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Core.Time;
using Meili.Mac.Platform;
using Meili.App.Tests;
namespace Meili.Mac.Tests;
public sealed class QuotePanelTests
{
    [AvaloniaTheory]
    [InlineData("zh-CN",false)] [InlineData("en-US",true)] [InlineData("zh-CN",true)] [InlineData("en-US",false)]
    public async Task Real_panel_renders_all_symbols_changes_intraday_and_keeps_hidden_work_stopped(string language,bool dark)
    {
        MacHost.EnsureComposed();var loc=LocalizationService.Current;loc.Apply(language);var theme=AppServices.Get<MacTheme>();theme.Apply(dark?AppThemeMode.Dark:AppThemeMode.Light);
        var data=new QuotePanelMarketData();var hub=new QuoteHub(data);var context=new SymbolContext();context.Set("AAPL");var store=new SettingsStore(Path.Combine(MacHost.DataDir,"panel"));var watch=new WatchlistStore(store);var model=new QuotePanelViewModel(hub,data,watch,context,loc);var opened="";var w=new QuotePanelWindow(model,s=>opened=s,()=>{},()=>{});
        try
        {
            await w.ShowPanelAsync();await Task.Delay(220);MacHost.Pump();Assert.Equal(6,model.Rows.Count);Assert.All(model.Rows,r=>Assert.True(r.Graph.Points.Count>10));Assert.Equal(6,hub.SubscribedSymbols.Count);Assert.All(w.GetVisualDescendants().OfType<TextBlock>().Where(t=>t.IsEffectivelyVisible),t=>Assert.True(t.FontSize>=12));
            MacHost.Save(w,$"quote-panel-{language}-{dark}");model.Select("NVDA");MacHost.Pump();Assert.Equal("NVDA",model.Selected!.Symbol);Assert.NotEqual(model.Rows[0].Graph.Points[10].Y,model.Rows[^1].Graph.Points[10].Y);
            w.HidePanel();Assert.Empty(hub.SubscribedSymbols);var count=data.Bars;await hub.PollAsync();Assert.Equal(count,data.Bars);
            await w.ShowPanelAsync();Assert.Equal(count,data.Bars);w.HidePanel();
        }
        finally{w.Close();loc.Apply("zh-CN");theme.Apply(AppThemeMode.System);}
    }
    [AvaloniaFact] public async Task Chart_work_is_bounded_cancels_on_hide_and_paging_only_subscribes_visible_symbols()
    {
        MacHost.EnsureComposed();var data=new QuotePanelMarketData{Block=true};var hub=new QuoteHub(data);var context=new SymbolContext();context.Set("AAPL");var settings=new SettingsStore(Path.Combine(MacHost.DataDir,"panel-bounds"));var watch=new WatchlistStore(settings);
        using var model=new QuotePanelViewModel(hub,data,watch,context,LocalizationService.Current);
        var loading=model.ActivateAsync();await Task.Delay(50);MacHost.Pump();Assert.InRange(data.MaxFlight,1,2);model.Deactivate();await loading;Assert.Empty(hub.SubscribedSymbols);Assert.Equal(0,data.InFlight);Assert.False(model.IsBusy);
        data.Block=false;await model.ActivateAsync();Assert.All(model.Rows,r=>Assert.NotEmpty(r.Graph.Points));
        foreach(var symbol in new[]{"TESTA","TESTB","TESTC"})watch.AddSymbol(watch.Groups[0].Id,symbol,out _);
        await model.MoveAsync(1);Assert.Equal(2,model.Pages);Assert.Single(model.Rows);Assert.Single(hub.SubscribedSymbols);
    }
}
