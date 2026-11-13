using Meili.Analytics;
using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Core.Pricing;
using Meili.Core.Settings;
using Meili.Data.Cache;
using Meili.Shared.ViewModels;
using ARight = Meili.Analytics.OptionRight;
using CRight = Meili.Core.Models.OptionRight;

namespace Meili.Shared.Tests;

[Collection(nameof(UiPlatformCollection))]
public class PricingSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "meili-pricing-" + Guid.NewGuid().ToString("N"));
    private readonly LocalizationService _loc = new();

    public PricingSnapshotTests() => Ui.Platform = new FakeUiPlatform();

    [Fact]
    public void Delayed_zero_day_quote_recovers_its_snapshot_iv_instead_of_doubling_it()
    {
        var snapshot = new DateTimeOffset(2026, 11, 13, 20, 40, 0, TimeSpan.Zero);
        var received = snapshot.AddMinutes(15);
        var expiration = new DateOnly(2026, 11, 13);
        var chain = new OptionChain { Underlying = "TEST", Contracts = [], Expirations = [expiration], AsOf = snapshot, DelayMinutes = 15 };
        var mid = BlackScholes.Price(ARight.Call, 100, 100, OptionTime.YearsToExpiry(expiration, snapshot), 0, 0, 0.3);
        var inputs = new PricingInputs(new RiskFreeRate(0, RateOrigin.Fixed, null), 0);
        foreach (var clock in new[] { received, received.AddMinutes(10) })
        {
            var result = OptionEnricher.Enrich(ARight.Call, 100, 100, expiration, null, mid, mid, mid, settings: inputs.Enrichment(clock, chain));
            Assert.Equal(0.3, result.ImpliedVolatility!.Value, 6);
        }
    }

    [Fact]
    public void Snapshot_clock_uses_declared_delay_only_when_the_market_timestamp_is_missing()
    {
        var received = new DateTimeOffset(2026, 11, 13, 20, 55, 0, TimeSpan.Zero);
        var chain = new OptionChain { Underlying = "TEST", Contracts = [], Expirations = [], DelayMinutes = 15, FetchedAt = received };
        var inputs = new PricingInputs(new RiskFreeRate(0, RateOrigin.Fixed, null), 0);
        Assert.Equal(received.AddMinutes(-15), inputs.Enrichment(received.AddMinutes(2), chain).Now);
        Assert.Equal(received.AddMinutes(-20), inputs.Enrichment(received, chain with { AsOf = received.AddMinutes(-20) }).Now);
        Assert.Equal(received, inputs.Enrichment(received, chain with { AsOf = received.AddMinutes(1) }).Now);
        Assert.Equal(received, inputs.Enrichment(received, chain with { DelayMinutes = null }).Now);
        Assert.Equal(received, inputs.Enrichment(received).Now);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Mixed_expiry_strategy_keeps_a_valid_first_expiry_curve_without_claiming_zero_margin()
    {
        var data = new SnapshotData();
        var settings = new SettingsStore(_directory);
        var rate = new RiskFreeRateProvider(data, () => new PricingSettings { RateMode = RiskFreeRateMode.Fixed, FixedRatePercent = 0 });
        var vm = new StrategyViewModel(new WatchlistStore(settings), data, _loc, new SymbolContext(), new PricingAssumptions(data, rate), new StrategyStore(_directory));
        var first = new DateOnly(2099, 12, 18);
        var second = first.AddDays(28);
        vm.Spot = 100;
        vm.Legs.Add(StrategyLegRow.From(StrategyLeg.Call(PositionSide.Long, 100, 3, first, 1, 0.3), vm));
        vm.Legs.Add(StrategyLegRow.From(StrategyLeg.Call(PositionSide.Short, 100, 5, second, 1, 0.3), vm));
        vm.OnLegEdited();

        Assert.Equal("—", vm.MarginText);
        Assert.Equal("—", vm.MaxLossText);
        Assert.Equal("—", vm.MaxProfitText);
        Assert.Null(vm.ExpiryAnalysis);
        Assert.True(vm.HasCurve, vm.StatusText);
        var index = Array.IndexOf(vm.Curve!.Prices, 100);
        Assert.True(index >= 0);
        Assert.InRange(vm.Curve.ExpiryPnl[index], -131.40, -131.38);
        Assert.NotEmpty(vm.MarginTip);
        vm.Deactivate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Options_use_the_snapshot_time_for_local_iv_and_preserve_provider_iv(bool providerIv)
    {
        var snapshot = new DateTimeOffset(2020, 11, 13, 20, 40, 0, TimeSpan.Zero);
        var expiration = new DateOnly(2020, 11, 13);
        var mid = BlackScholes.Price(ARight.Call, 100, 100, OptionTime.YearsToExpiry(expiration, snapshot), 0, 0, 0.4);
        var data = new SnapshotData
        {
            Chain = new OptionChain
            {
                Underlying = "TEST", UnderlyingPrice = 100, Expiration = expiration, Expirations = [expiration],
                Source = "test", AsOf = snapshot, DelayMinutes = 15,
                Contracts = [new OptionContract
                {
                    ContractSymbol = "TESTCALL", Underlying = "TEST", Right = CRight.Call, Strike = 100, Expiration = expiration,
                    Bid = mid - 0.00001, Ask = mid + 0.00001, Last = mid, ImpliedVolatility = providerIv ? 0.4 : null
                }]
            }
        };
        var settings = new SettingsStore(_directory);
        var rate = new RiskFreeRateProvider(data, () => new PricingSettings { RateMode = RiskFreeRateMode.Fixed, FixedRatePercent = 0 });
        var pricing = new PricingAssumptions(data, rate);
        var insights = new VolatilityInsights(new NoIvSource(), data, rate, new MarketDb(Path.Combine(_directory, "market.db")));
        var options = new OptionsViewModel(new WatchlistStore(settings), data, _loc, new SymbolContext(), pricing, settings, insights) { SymbolText = "TEST" };
        await options.LoadAsync(refreshOnly: true);
        Assert.True(options.HasRows, options.StatusText);
        Assert.Equal(0.4, options.Rows.Single().Call!.IvValue!.Value, 6);
        options.Deactivate();

        var strategy = new StrategyViewModel(new WatchlistStore(settings), data, _loc, new SymbolContext(), pricing, new StrategyStore(_directory));
        strategy.Seed(new StrategySeed("TEST", expiration, CRight.Call, PositionSide.Long, 100));
        await strategy.LoadAsync();
        Assert.Equal("40.0", strategy.Legs.Single().IvText);
        strategy.Deactivate();
    }

    [Fact]
    public async Task Atm_alerts_and_term_structure_use_the_chain_snapshot_clock()
    {
        var snapshot = new DateTimeOffset(2026, 11, 13, 20, 40, 0, TimeSpan.Zero);
        var received = snapshot.AddMinutes(15);
        var expiration = new DateOnly(2026, 12, 13);
        var mid = BlackScholes.Price(ARight.Call, 100, 100, OptionTime.YearsToExpiry(expiration, snapshot), 0, 0, 0.4);
        var data = new SnapshotData
        {
            Chain = new OptionChain
            {
                Underlying = "TEST", UnderlyingPrice = 100, AsOf = snapshot, Expirations = [expiration], Contracts = [new OptionContract
                {
                    ContractSymbol = "TESTCALL", Underlying = "TEST", Strike = 100, Right = CRight.Call, Expiration = expiration,
                    Bid = mid, Ask = mid, Last = mid
                }]
            }
        };
        var rate = new RiskFreeRateProvider(data, () => new PricingSettings { RateMode = RiskFreeRateMode.Fixed, FixedRatePercent = 0 });
        var source = new IvSource((_, _) => Task.FromResult<IReadOnlyList<Quote>>([]), data, rate);
        var sample = await source.GetAsync("TEST", received);
        Assert.Equal(0.4, sample!.Iv, 6);
        Assert.Equal(snapshot, sample.AsOf);
        var insights = new VolatilityInsights(source, data, rate, new MarketDb(Path.Combine(_directory, "market.db")));
        var term = await insights.TermStructureAsync("TEST", [expiration], 100, received, CancellationToken.None);
        Assert.Equal(0.4, term.Single().Iv, 6);
    }

    private sealed class NoIvSource : IIvSource
    {
        public Task<Meili.Core.Alerts.IvSample?> GetAsync(string symbol, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<Meili.Core.Alerts.IvSample?>(null);
    }

    internal sealed class SnapshotData : IMarketDataService
    {
        public OptionChain Chain { get; set; } = null!;

        public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Quote>>(symbols.Select(symbol => new Quote { Symbol = symbol, Price = 100, Source = "test" }).ToArray());

        public Task<OptionChain> GetChainAsync(string symbol, DateOnly? expiration = null, CancellationToken ct = default) => Task.FromResult(Chain);
        public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string symbol, CancellationToken ct = default) => Task.FromResult(Chain.Expirations);
        public Task<Fundamentals> GetFundamentalsAsync(string symbol, CancellationToken ct = default) => Task.FromResult(new Fundamentals { Symbol = symbol, DividendYield = 0 });
        public Task<BarSeries> GetBarsAsync(BarRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FinancialStatements> GetFinancialsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, int count = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
