using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Meili.Mac.Platform;
using Meili.Mac.Views;

namespace Meili.Mac.Tests;

public class WorkbenchPageTests
{
    [AvaloniaFact]
    public void Workbench_is_default_and_detaching_or_switching_details_releases_private_session()
    {
        MacHost.EnsureComposed();
        var page=new StrategyPage();var window=new Window{Width=1280,Height=900,Content=page};
        try
        {
            window.Show();MacHost.Pump(10);
            var control=page.FindControl<WebChartControl>("Workspace")!;
            Assert.NotNull(control.Session);Assert.True(control.Session!.IsStrategyWorkbench);
            var model=page.DataContext;var session=control.Session;
            page.ShowDetailedAnalysis();MacHost.Pump();
            Assert.Null(control.Session);Assert.Same(model,page.DataContext);
            page.FindControl<TabControl>("SurfaceTabs")!.SelectedIndex=0;MacHost.Pump();
            Assert.NotNull(control.Session);Assert.NotSame(session,control.Session);
            window.Content=null;MacHost.Pump();Assert.Null(control.Session);
        }
        finally{window.Close();}
    }
}
