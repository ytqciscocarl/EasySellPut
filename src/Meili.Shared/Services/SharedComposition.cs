using Microsoft.Extensions.DependencyInjection;
using Meili.Core.Abstractions;
using Meili.Core.Settings;
using Meili.Data;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Services;

/// <summary>DI registrations common to the Windows and macOS front ends (each adds its theme, notifier and UI platform).</summary>
public static class SharedComposition
{
    public static void Register(IServiceCollection s, DataDirectoryInfo dataDir, LocalizationService loc)
    {
        s.AddSingleton(dataDir);
        s.AddSingleton(new SettingsStore(dataDir.Path));
        s.AddSingleton(new AlertStore(dataDir.Path));
        s.AddSingleton(new ChartDrawingStore(dataDir.Path));
        s.AddSingleton(new StrategyStore(dataDir.Path));   // B7
        s.AddSingleton<PricingAssumptions>();              // H1
        s.AddSingleton(loc);
        s.AddSingleton<WatchlistStore>();
        s.AddSingleton<NavigationHub>();
        s.AddSingleton<SymbolContext>();
        s.AddSingleton<AlertMonitor>();
        s.AddSingleton<ShellViewModel>();
        s.AddSingleton(sp => new DataServices(sp.GetRequiredService<SettingsStore>().Current.Network));
        s.AddSingleton(sp => sp.GetRequiredService<DataServices>().Router);
        // B2: K-line history is cached in SQLite in the data folder; everything else goes straight to the router.
        s.AddSingleton(sp => new Meili.Data.Cache.MarketDb(System.IO.Path.Combine(dataDir.Path, "cache", "market.db")));
        s.AddSingleton<IMarketDataService>(sp => new Meili.Data.Cache.CachedMarketData(
            sp.GetRequiredService<DataServices>().Router, sp.GetRequiredService<Meili.Data.Cache.MarketDb>()));
        s.AddSingleton(sp => new Meili.Core.Quotes.QuoteHub(sp.GetRequiredService<IMarketDataService>()));
        s.AddSingleton<DesktopActivity>();
        s.AddSingleton<QuotePump>();
        s.AddSingleton<SymbolSearchService>();
        s.AddSingleton(sp => new Meili.Core.Pricing.RiskFreeRateProvider(sp.GetRequiredService<IMarketDataService>(),
            () => sp.GetRequiredService<SettingsStore>().Current.Pricing));
        s.AddSingleton<IIvSource>(sp => new IvSource(
            (symbols, ct) => sp.GetRequiredService<DataServices>().Providers.OfType<Meili.Data.Providers.Cboe.CboeProvider>().First().GetQuotesAsync(symbols, ct),
            sp.GetRequiredService<IMarketDataService>(), sp.GetRequiredService<Meili.Core.Pricing.RiskFreeRateProvider>()));
        s.AddSingleton<VolatilityInsights>();
        s.AddSingleton<IvAlertSampler>();
        s.AddSingleton<NetworkController>();
        s.AddSingleton<INetworkController>(sp => sp.GetRequiredService<NetworkController>());
        s.AddSingleton<UpdateViewModel>();
        s.AddTransient<SettingsViewModel>();
        s.AddTransient<DashboardViewModel>();
        s.AddTransient<QuotePanelViewModel>();
        s.AddSingleton<WatchlistViewModel>();
        s.AddSingleton<ChartsViewModel>();
        s.AddSingleton<OptionsViewModel>();
        s.AddSingleton<StrategyViewModel>();
        s.AddSingleton<NewsViewModel>();
        s.AddSingleton<AlertsViewModel>();
        s.AddSingleton<DataSourcesViewModel>();
    }

    /// <summary>After the window is shown: detect the proxy (Clash etc.) in the background without blocking startup.</summary>
    public static void OnStarted(IServiceProvider sp)
    {
        _ = sp.GetRequiredService<NetworkController>().ReconfigureAsync();
        sp.GetRequiredService<AlertStore>().Load();
        sp.GetRequiredService<AlertMonitor>().Start();
        sp.GetRequiredService<IvAlertSampler>().Start();
        sp.GetRequiredService<QuotePump>().Start();
        sp.GetRequiredService<UpdateViewModel>().StartNotifications();
    }
}