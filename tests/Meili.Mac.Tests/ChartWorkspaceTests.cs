using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views;
using Meili.Shared.ViewModels;

namespace Meili.Mac.Tests;

public sealed class ChartWorkspaceTests
{
    [AvaloniaTheory]
    [InlineData(980, 620, "zh-CN", false)]
    [InlineData(980, 620, "en-US", true)]
    [InlineData(1320, 840, "zh-CN", true)]
    [InlineData(1320, 840, "en-US", false)]
    public async Task Chart_keeps_the_space_and_settings_remain_accessible(int width,int height,string language,bool dark)
    {
        MacHost.EnsureComposed();
        var loc=AppServices.Get<LocalizationService>();var original=loc.Language;
        var theme=AppServices.Get<MacTheme>();var vm=AppServices.Get<ChartsViewModel>();
        vm.ShowInspector=false;vm.DrawMode=null;loc.Apply(language);theme.Apply(dark?AppThemeMode.Dark:AppThemeMode.Light);
        var window=new MainWindow{Width=width,Height=height};
        try
        {
            window.Show();window.Navigate(AppPage.Charts);MacHost.Pump(15);
            var page=(ChartsPage)window.CurrentView!;
            var chart=page.FindControl<WebChartControl>("Chart")!;
            var inspector=page.FindControl<Border>("ChartInspector")!;
            var toggle=page.FindControl<ToggleButton>("InspectorToggle")!;
            Assert.False(inspector.IsVisible);
            var toolbar=page.FindControl<Grid>("ChartToolbar")!;
            var tabs=page.FindControl<ItemsControl>("RangeTabs")!;
            Assert.True(toolbar.Bounds.Height<=56,$"Toolbar unexpectedly gained a row: {toolbar.Bounds}");
            var rangeButtons=tabs.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(8,rangeButtons.Length);
            Assert.All(rangeButtons,b=>Assert.InRange(b.TranslatePoint(new Point(),page)!.Value.Y-rangeButtons[0].TranslatePoint(new Point(),page)!.Value.Y,-1,1));
            var input=page.FindControl<Meili.Mac.Views.Controls.SymbolBox>("SymbolInput")!;
            Assert.InRange(Math.Abs(input.TranslatePoint(new Point(),page)!.Value.Y-toggle.TranslatePoint(new Point(),page)!.Value.Y),0,18);

            Assert.True(chart.Bounds.Height>page.Bounds.Height*.72,$"Chart height: {chart.Bounds}, page: {page.Bounds}");
            Assert.True(chart.Bounds.Width>page.Bounds.Width*.82);
            Assert.Equal(loc["Ch_Tools"],toggle.Content);
            MacHost.Save(window,$"workspace-{width}-{language}-{dark}-default");
            if(Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is {Length:>0} renderDir)
            {
                using var toolbarFrame=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(toolbar.Bounds.Width),(int)Math.Ceiling(toolbar.Bounds.Height)));
                toolbarFrame.Render(toolbar);
                toolbarFrame.Save(Path.Combine(renderDir,$"toolbar-{width}-{language}-{dark}.png"),new PngBitmapEncoderOptions());
            }
            rangeButtons[^1].Focus(NavigationMethod.Tab);MacHost.Pump();
            var scroller=page.FindControl<ScrollViewer>("RangeScroller")!;
            var lastX=rangeButtons[^1].TranslatePoint(new Point(),scroller)!.Value.X;
            Assert.InRange(lastX,-1,scroller.Bounds.Width-rangeButtons[^1].Bounds.Width+2);

            var fullWidth=chart.Bounds.Width;
            toggle.IsChecked=true;MacHost.Pump();
            Assert.True(vm.ShowInspector);Assert.True(inspector.IsVisible);
            Assert.InRange(fullWidth-chart.Bounds.Width,279,281);
            Assert.True(chart.Bounds.Width>=400);
            Assert.True(page.FindControl<TextBox>("CompareBox")!.IsEffectivelyVisible);
            var draw=page.GetVisualDescendants().OfType<ToggleButton>().First(b=>Equals(b.CommandParameter,DrawingKind.HorizontalLine));
            Assert.Equal(Color.FromUInt32(Meili.Shared.Services.ThemePalette.For(dark).Control),((ISolidColorBrush)draw.Background!).Color);
            draw.Command!.Execute(draw.CommandParameter);await Task.Delay(160);MacHost.Pump();Assert.True(draw.IsChecked);
            Assert.Equal(Color.FromUInt32(Meili.Shared.Services.ThemePalette.For(dark).RowHighlight),((ISolidColorBrush)draw.Background!).Color);
            vm.DrawMode=null;MacHost.Pump();Assert.False(draw.IsChecked);
            MacHost.Save(window,$"workspace-{width}-{language}-{dark}-inspector");
            toggle.IsChecked=false;MacHost.Pump();Assert.False(vm.ShowInspector);
            Assert.Equal(fullWidth,chart.Bounds.Width);
        }
        finally{window.Close();vm.ShowInspector=false;vm.DrawMode=null;loc.Apply(original);theme.Apply(AppThemeMode.System);}
    }
}
