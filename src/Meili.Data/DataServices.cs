using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Data.Http;
using Meili.Data.Providers;
using Meili.Data.Providers.Yahoo;
using Meili.Data.Routing;

namespace Meili.Data;

/// <summary>Composition of the whole data layer: one gateway, one cache, all providers, health tracking and the failover router.</summary>
public sealed class DataServices : IDisposable
{
    /// <summary>Failover order per capability (docs/01 §5.3). Ids that a build doesn't contain are ignored.</summary>
    public static readonly IReadOnlyDictionary<DataCapability, string[]> DefaultOrder = new Dictionary<DataCapability, string[]>
    {
        [DataCapability.Quote] = ["yahoo", "cnbc", "nasdaq", "sina", "tencent", "eastmoney", "cboe"],
        [DataCapability.IntradayBars] = ["yahoo", "nasdaq", "cboe", "tencent"],
        [DataCapability.DailyBars] = ["yahoo", "cboe", "nasdaq", "tencent", "sina", "eastmoney"],
        [DataCapability.Options] = ["cboe", "yahoo", "nasdaq"],
        [DataCapability.Fundamentals] = ["yahoo", "nasdaq", "cnbc"],
        [DataCapability.Financials] = ["yahoo", "nasdaq", "sec"],
        [DataCapability.News] = ["yahoo", "nasdaq"],
        [DataCapability.Search] = ["yahoo", "sec"],
    };

    public HttpGateway Gateway { get; }
    public ResponseCache Cache { get; } = new();
    public HealthTracker Health { get; } = new();
    public IReadOnlyList<ProviderBase> Providers { get; }
    public FailoverRouter Router { get; }

    public DataServices(NetworkSettings settings, ProxyResolution? proxy = null, HttpGateway? gateway = null)
    {
        Gateway = gateway ?? new HttpGateway(settings, proxy);
        Providers = CreateProviders(Gateway, Cache);
        foreach (var p in Providers) p.RegisterRateLimit();
        Router = new FailoverRouter(Providers, Health, DefaultOrder)
        {
            QuoteSourceBudget = () => TimeSpan.FromSeconds(Gateway.Settings.RequestTimeoutSeconds * (Gateway.Settings.MaxRetries + 1)),
        };
        Health.Attach(Gateway);
    }

    private static IReadOnlyList<ProviderBase> CreateProviders(HttpGateway http, ResponseCache cache) =>
    [
        new YahooProvider(http, cache),
        new Providers.Cboe.CboeProvider(http, cache),
        new Providers.Nasdaq.NasdaqProvider(http, cache),
        new Providers.Cnbc.CnbcProvider(http, cache),
        new Providers.Sec.SecProvider(http, cache),
        new Providers.China.SinaProvider(http, cache),
        new Providers.China.TencentProvider(http, cache),
        new Providers.China.EastmoneyProvider(http, cache),
    ];

    public void Dispose() => Gateway.Dispose();
}