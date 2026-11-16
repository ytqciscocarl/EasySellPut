using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Meili.Core.Quotes;
using Meili.Mac.Platform;
namespace Meili.Mac.Tests;
public sealed class BackgroundLifecycleTests
{
    private static async Task Settle(){for(var i=0;i<6;i++){await Task.Delay(40);MacHost.Pump();}}
    private static async Task<double[]> CpuSamples(Process process)
    {
        var values=new List<double>();for(var i=0;i<3;i++){var before=process.TotalProcessorTime;await Task.Delay(1000);values.Add((process.TotalProcessorTime-before).TotalMilliseconds);}return values.ToArray();
    }
    [AvaloniaFact] public async Task Background_releases_private_charts_stops_chrome_and_restores_the_same_page()
    {
        MacHost.EnsureComposed();var w=new MainWindow();w.Show();w.Width=1100;w.Height=740;w.Navigate(AppPage.Charts);await Settle();var page=w.CurrentView!;var vm=page.DataContext;var chart=page.FindControl<WebChartControl>("Chart")!;var shell=AppServices.Get<ShellViewModel>();var hub=AppServices.Get<QuoteHub>();
        try
        {
            Assert.NotNull(chart.Session);Assert.True(shell.IsClockRunning);MacHost.Save(w,"background-before");
            var process=Process.GetCurrentProcess();var foregroundMs=await CpuSamples(process);
            w.RunInBackground();await Settle();Assert.True(w.PresentationSuspended);Assert.Null(chart.Session);Assert.False(shell.IsClockRunning);Assert.Empty(hub.SubscribedSymbols);var requests=hub.RequestCount;var scans=shell.McpScanCount;await Task.Delay(1000);
            var hiddenMs=await CpuSamples(process);Assert.Equal(scans,shell.McpScanCount);Assert.Null(await hub.PollAsync());Assert.Equal(requests,hub.RequestCount);
            for(var i=0;i<3;i++){w.ShowFromDock();await Settle();Assert.Same(page,w.CurrentView);Assert.Same(vm,w.CurrentView!.DataContext);Assert.NotNull(chart.Session);Assert.True(shell.IsClockRunning);w.Hide();await Settle();Assert.Null(chart.Session);Assert.Empty(hub.SubscribedSymbols);}
            w.ShowFromDock();await Settle();MacHost.Save(w,"background-restored");
            if(Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is {Length:>0} dir)File.WriteAllText(Path.Combine(dir,"background-profile.json"),JsonSerializer.Serialize(new{sampleDurationMs=1000,foregroundCpuMs=foregroundMs,backgroundCpuMs=hiddenMs,hiddenMcpScans=0,managedBytes=GC.GetTotalMemory(false),workingSet=process.WorkingSet64,hiddenChartSessions=0,hiddenChromeTimers=0,hiddenUiQuoteSubscribers=0,hardwareClaim="Short diagnostic sample, not an FPS/energy benchmark"}));
        }
        finally{w.AllowClose();w.Close();}
    }
}
