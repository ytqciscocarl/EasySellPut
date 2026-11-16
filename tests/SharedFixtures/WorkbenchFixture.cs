using Meili.Analytics;
using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Core.Pricing;
using Meili.Core.Settings;
using Meili.Shared.Services;
using Meili.Shared.ViewModels;

namespace Meili.Testing;

internal static class WorkbenchFixture
{
    public static async Task<StrategyViewModel> CreateAsync(string path)
    {
        var data=new Data();var settings=new SettingsStore(path);
        var rates=new RiskFreeRateProvider(data,()=>new PricingSettings{RateMode=RiskFreeRateMode.Fixed,FixedRatePercent=0});
        var vm=new StrategyViewModel(new WatchlistStore(settings),data,LocalizationService.Current,new SymbolContext(),new PricingAssumptions(data,rates),new StrategyStore(path),settings);
        vm.SymbolText="AAPL";vm.SelectedPreset=vm.Presets.First(p=>p.Kind==StrategyKind.Custom);
        await vm.LoadAsync();return vm;
    }
    public static async Task<ChartsViewModel> CreateChartAsync(string path,bool fullSession=false)
    {
        var settings=new SettingsStore(path);
        var vm=new ChartsViewModel(new WatchlistStore(settings),new Data(fullSession),LocalizationService.Current,new SymbolContext(),settings,new ChartDrawingStore(path));
        vm.SymbolText=fullSession?"FIXTURE":"AAPL";if(fullSession)vm.ShowVolume=vm.ShowMacd=vm.ShowRsi=vm.ShowKdj=true;await vm.LoadAsync();return vm;
    }
    public static ChartPalette Palette(bool dark)
    {
        var p=ThemePalette.For(dark);ScottPlot.Color C(uint n)=>new((byte)(n>>16),(byte)(n>>8),(byte)n);
        return ChartPalette.Create(dark,C(p.Up),C(p.Down),C(p.PrimaryText),C(p.SecondaryText),p);
    }
    private sealed class Data(bool fullSession=false):IMarketDataService
    {
        private readonly DateOnly _expiry=new(2099,12,18);
        public Task<OptionChain> GetChainAsync(string symbol,DateOnly? expiration=null,CancellationToken ct=default)
        {
            var contracts=new List<OptionContract>();
            foreach(var strike in Enumerable.Range(16,9).Select(i=>i*5))
            foreach(var right in new[]{Meili.Core.Models.OptionRight.Call,Meili.Core.Models.OptionRight.Put})
            {
                var price=right==Meili.Core.Models.OptionRight.Call?(strike==100?3:strike==110?1.5:Math.Max(100-strike,0)+2):Math.Max(strike-100,0)+2;
                contracts.Add(new(){ContractSymbol=$"{right}{strike}",Underlying=symbol,Right=right,Strike=strike,Expiration=_expiry,Bid=price-.25,Ask=price+.25,ImpliedVolatility=.25,OpenInterest=1000,Volume=100});
            }
            return Task.FromResult(new OptionChain{Underlying=symbol,UnderlyingPrice=100,Expiration=_expiry,Expirations=[_expiry],Contracts=contracts,Source="fixture",AsOf=DateTimeOffset.UtcNow.AddMinutes(-15),DelayMinutes=15});
        }
        public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<Quote>>(symbols.Select(s=>new Quote{Symbol=s,Price=100,Source="fixture"}).ToArray());
        public Task<Fundamentals> GetFundamentalsAsync(string symbol,CancellationToken ct=default)=>Task.FromResult(new Fundamentals{Symbol=symbol,DividendYield=0,Source="fixture",NextEarningsDate=new(2025,1,6),EarningsDateStatus=EventDateStatus.Estimated,ExDividendDate=new(2099,1,1)});
        public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string symbol,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<DateOnly>>([_expiry]);
        public Task<BarSeries> GetBarsAsync(BarRequest request,CancellationToken ct=default)=>Task.FromResult(new BarSeries{Symbol=request.Symbol,Interval=BarInterval.Minute5,Source="fixture",Bars=Enumerable.Range(0,fullSession?192:120).Select(i=>{var price=fullSession?100+Math.Sin(i/11.0)*2+i*.025:100+i;return Bar.FromSource(new DateTimeOffset(2025,1,6,fullSession?9:14,fullSession?0:30,0,TimeSpan.Zero).AddMinutes(i*5),price,price+1,price-1,price+.5,1000);}).ToArray()});
        public Task<FinancialStatements> GetFinancialsAsync(string symbol,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol,int count=20,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<NewsItem>>([new(){Title="Fixture news <script>literal</script>",Url="https://example.com/news",Source="fixture",Publisher="Test publisher",Published=new(2025,1,6,16,0,0,TimeSpan.Zero),Summary="News summary"},new(){Title="Unknown time",Url="https://example.com/unknown",Source="fixture"}]);
        public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query,int limit=10,CancellationToken ct=default)=>throw new NotSupportedException();
    }
}
