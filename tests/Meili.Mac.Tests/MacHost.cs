using Avalonia;
using Avalonia.Headless;
using Microsoft.Extensions.DependencyInjection;
using Meili.App.Tests;
using Meili.Core.Abstractions;
using Meili.Core.Settings;
using Meili.Mac.Platform;

[assembly: AvaloniaTestApplication(typeof(Meili.Mac.Tests.MacHost))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
// One Application for the whole run, like the real app: the DI singletons (theme, strings) attach to it once.
// The default (PerTest) recreates Application per test, leaving them bound to the first, already disposed one.
[assembly: Avalonia.Headless.AvaloniaTestIsolation(Avalonia.Headless.AvaloniaTestIsolationLevel.PerAssembly)]

namespace Meili.Mac.Tests;

/// <summary>
/// Headless Avalonia (real Skia rendering, no window on any screen) with the real Mac composition, offline fakes for
/// market data / IV / network, and a scratch data folder.
/// </summary>
public static class MacHost
{
    public static FakeMarketDataService Data { get; } = new();
    public static string DataDir { get; } = Path.Combine(Path.GetTempPath(), "meili-mac-tests-" + Environment.ProcessId);
    private static bool _composed;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Builds the services once per test process (on the headless UI thread).</summary>
    public static void EnsureComposed()
    {
        if (_composed) return;
        _composed = true;
        Directory.CreateDirectory(DataDir);
        var dataDir = new DataDirectoryInfo(DataDir, DataDirectorySource.CommandLine);
        AppServices.DataDirectory = dataDir;
        var services = new ServiceCollection();
        services.AddSingleton(new AppLaunchOptions { NoAlerts = true, ExitOnClose = true });
        MacComposition.Register(services, dataDir);
        services.AddSingleton<IMarketDataService>(Data);
        services.AddSingleton<IIvSource>(new FakeIvSource());
        services.AddSingleton<INetworkController>(new FakeNetwork());
        AppServices.Provider = services.BuildServiceProvider();
        App.Boot();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            (AppServices.Provider as IDisposable)?.Dispose();
            try { Directory.Delete(DataDir, true); } catch (IOException) { } // expected: SQLite may still hold the cache file at exit
        };
    }

    /// <summary>Off-screen render of the headless window to MEILI_RENDER_DIR (when set), for review.</summary>
    public static void Save(Avalonia.Controls.Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("MEILI_RENDER_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Pump(10);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, $"mac-{name}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>Lets queued UI work, bindings, layout and a render pass complete.</summary>
    public static void Pump(int rounds = 5)
    {
        for (var i = 0; i < rounds; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
