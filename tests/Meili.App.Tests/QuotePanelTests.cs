using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Meili.App.Views;
using Meili.Core.Quotes;
using Meili.Core.Settings;
namespace Meili.App.Tests;
public sealed class QuotePanelTests
{
    [Fact] public void Tray_panel_uses_real_native_layout_selection_and_visibility_scoped_subscriptions()
    {
        WpfHost.Run(async()=>
        {
            ScreenRenderTests.Apply("en-US",true);var data=new QuotePanelMarketData();var hub=new QuoteHub(data);var settings=new SettingsStore(Path.Combine(Path.GetTempPath(),"quote-panel-"+Guid.NewGuid()));var watch=new WatchlistStore(settings);var context=new SymbolContext();context.Set("AAPL");
            using var model=new QuotePanelViewModel(hub,data,watch,context,LocalizationService.Current);var panel=new QuotePanelWindow(model,_=>{},()=>{},()=>{});
            try{await panel.ShowPanelAsync();await WpfHost.Settle();Assert.True(panel.IsVisible);Assert.Equal(6,model.Rows.Count);Assert.Equal(6,hub.SubscribedSymbols.Count);
            var workspace=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");var directory=Environment.GetEnvironmentVariable("MEILI_RENDER_DIR")??(workspace is null?null:Path.Combine(workspace,"build","out","ci"));
            if(directory is not null){await Task.Delay(250);await WpfHost.Settle();Directory.CreateDirectory(directory);var bitmap=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,"desktop-smoke-quote-panel-windows.png"));encoder.Save(file);}
            model.Select("NVDA");Assert.Equal("NVDA",model.Selected!.Symbol);panel.HidePanel();Assert.Empty(hub.SubscribedSymbols);Assert.False(panel.IsVisible);}
            finally{panel.Close();}
        });
    }
}
