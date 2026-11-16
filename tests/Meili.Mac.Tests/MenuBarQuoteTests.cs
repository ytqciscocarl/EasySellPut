using Avalonia.Headless.XUnit;
using Meili.App.Tests;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Mac.Platform;

namespace Meili.Mac.Tests;
public sealed class MenuBarQuoteTests
{
    [AvaloniaFact]
    public void Price_change_missing_data_delay_stale_and_extended_prices_are_honest()
    {
        MacHost.EnsureComposed();var loc=AppServices.Get<LocalizationService>();var now=DateTimeOffset.UtcNow;
        var quote=FakeMarketDataService.MakeQuote("NVDA",162.85) with{ChangePercent=1.2,DelayMinutes=15,IsRealtime=false};
        var view=MenuBarQuote.Create("NVDA",quote,false,loc,now);
        Assert.Contains("NVDA 162.85 ↑1.20%",view.Title);Assert.Contains("15m",view.Title);Assert.Equal(1,view.Direction);
        Assert.Contains("162.85",view.Tooltip);
        Assert.Equal("MSFT —",MenuBarQuote.Create("MSFT",quote,false,loc,now).Title);
        Assert.Equal("NVDA —",MenuBarQuote.Create("NVDA",quote with{Price=double.NaN},false,loc,now).Title);
        var missing=MenuBarQuote.Create("NVDA",quote with{ChangePercent=null,PreviousClose=null},false,loc,now);
        Assert.DoesNotContain("%",missing.Title);
        Assert.Equal(0,MenuBarQuote.Create("NVDA",quote,true,loc,now).Direction);
        Assert.Contains(loc["Mb_Stale"],MenuBarQuote.Create("NVDA",quote,true,loc,now).Title);
        var extended=MenuBarQuote.Create("NVDA",quote with{MarketState=MarketState.AfterHours,ExtendedPrice=170,ExtendedTime=null},false,loc,now);
        Assert.Contains("162.85",extended.Title);Assert.DoesNotContain("170",extended.Title);
        Assert.Contains(extended.Details,line=>line.Contains("170")&&line.Contains(loc["Quote_AsOfUnknown"]));
        Assert.DoesNotContain("Infinity",MenuBarQuote.Create("NVDA",quote with{Price=1e308,ChangePercent=null,PreviousClose=1e-300},false,loc,now).Title);
    }
    [AvaloniaFact]
    public async Task Subscriber_follows_symbol_uses_shared_pump_and_disposes_without_writing_preferences()
    {
        MacHost.EnsureComposed();var data=new FakeMarketDataService();var hub=new QuoteHub(data){Ttl=TimeSpan.Zero};
        var context=new SymbolContext();context.Set("AAPL");var settings=new SettingsStore(Path.Combine(MacHost.DataDir,"menu-tests"));
        var watchlist=new WatchlistStore(settings);var item=new Sink();var opened=0;
        var controller=new MenuBarQuoteController(hub,context,watchlist,AppServices.Get<LocalizationService>(),settings,item,()=>opened++,()=>{});
        try
        {
            Assert.Contains("AAPL",hub.SubscribedSymbols);Assert.StartsWith("AAPL ",item.Quote!.Title);
            context.Set("NVDA");Assert.Equal(new[]{"NVDA"},hub.SubscribedSymbols);Assert.StartsWith("NVDA ",item.Quote.Title);
            item.Entries.Single(e=>e.Title=="MSFT").Action!();Assert.Equal("MSFT",context.Current);Assert.StartsWith("MSFT ",item.Quote.Title);
            item.Entries.First(e=>e.Title==AppServices.Get<LocalizationService>()["Mb_OpenChart"]).Action!();Assert.Equal(1,opened);
            var theme=AppServices.Get<MacTheme>();
            theme.ApplyUpDownColors(UpDownColorMode.GreenUp);
            settings.Current.UpDownColors=UpDownColorMode.GreenUp;settings.Current.UpDownColorsChosen=true;
            MacHost.Pump();Assert.Equal(UpDownColorMode.GreenUp,item.Colors);
            theme.ApplyUpDownColors(AppServices.Get<SettingsStore>().Current.EffectiveUpDownColors());
            data.QuoteFailure=new IOException("fixture offline");await hub.PollAsync();MacHost.Pump();
            Assert.Contains(AppServices.Get<LocalizationService>()["Mb_Stale"],item.Quote.Title);
        }
        finally{controller.Dispose();}
        Assert.Empty(hub.SubscribedSymbols);Assert.True(item.Disposed);
    }
    private sealed class Sink:IQuoteStatusItem
    {
        public MenuBarQuote? Quote;public IReadOnlyList<QuoteMenuEntry> Entries=[];public bool Disposed;public UpDownColorMode Colors;
        public void Update(MenuBarQuote quote,IReadOnlyList<QuoteMenuEntry> entries,UpDownColorMode colors){Quote=quote;Entries=entries;Colors=colors;}
        public void Dispose()=>Disposed=true;
    }
}
