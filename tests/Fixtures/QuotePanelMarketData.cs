using Meili.Core.Models;
using Meili.Core.Time;
namespace Meili.App.Tests;
    public sealed class QuotePanelMarketData : Meili.Core.Abstractions.IMarketDataService
    {
        private readonly FakeMarketDataService _other=new();public int Bars;public bool Block;public int InFlight,MaxFlight;
        private static double Price(string s)=>s switch{"AAPL"=>356.27,"SPY"=>754.37,"QQQ"=>612.45,"MSFT"=>507.32,"NVDA"=>162.85,_=>421.06};
        private static double Delta(string s)=>s is "QQQ" or "NVDA"?-.67:.39;
        private static DateTime Date(){var now=MarketClock.ToEastern(DateTimeOffset.UtcNow);var day=now.Date;if(now.TimeOfDay<TimeSpan.FromHours(16))day=day.AddDays(-1);while(day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)day=day.AddDays(-1);return day;}
        public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<Quote>>(symbols.Select(s=>FakeMarketDataService.MakeQuote(s,Price(s)) with{Name=s switch{"AAPL"=>"Apple Inc.","NVDA"=>"NVIDIA Corp.","MSFT"=>"Microsoft Corp.","SPY"=>"SPDR S&P 500 ETF",_=>s},Change=Price(s)-Price(s)/(1+Delta(s)/100),ChangePercent=Delta(s),PreviousClose=Price(s)/(1+Delta(s)/100),Open=Price(s)*.997,High=Price(s)*1.008,Low=Price(s)*.985,Volume=35120000,MarketState=MarketState.Closed,AsOf=MarketClock.FromEastern(Date().AddHours(16)),DelayMinutes=15,IsRealtime=false}).ToArray());
        public async Task<BarSeries> GetBarsAsync(BarRequest request,CancellationToken ct=default){Bars++;if(Block){InFlight++;MaxFlight=Math.Max(MaxFlight,InFlight);try{await Task.Delay(Timeout.Infinite,ct);}finally{InFlight--;}}var start=MarketClock.FromEastern(Date().AddHours(9.5));var seed=request.Symbol.Sum(c=>(int)c);var price=Price(request.Symbol);return new BarSeries{Symbol=request.Symbol,Interval=request.Interval,Source="yahoo",DelayMinutes=15,AsOf=start.AddHours(6.5),FetchedAt=DateTimeOffset.UtcNow,Bars=Enumerable.Range(0,78).Select(i=>Bar.CloseOnly(start.AddMinutes(i*5),price*(1+.003*Math.Sin(i*.19+seed)+Delta(request.Symbol)*.0001*i),1000)).ToArray()};}
        public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string s,CancellationToken ct=default)=>_other.GetExpirationsAsync(s,ct);
        public Task<OptionChain> GetChainAsync(string s,DateOnly? d=null,CancellationToken ct=default)=>_other.GetChainAsync(s,d,ct);
        public Task<Fundamentals> GetFundamentalsAsync(string s,CancellationToken ct=default)=>_other.GetFundamentalsAsync(s,ct);
        public Task<FinancialStatements> GetFinancialsAsync(string s,CancellationToken ct=default)=>_other.GetFinancialsAsync(s,ct);
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string s,int count=20,CancellationToken ct=default)=>_other.GetNewsAsync(s,count,ct);
        public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string s,int limit=10,CancellationToken ct=default)=>_other.SearchAsync(s,limit,ct);
    }
