using System.Text.Json;
using Meili.Analytics;
using Meili.Automation;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Data.Cache;
using CRight=Meili.Core.Models.OptionRight;

namespace Meili.Automation.Tests;
public sealed class FullCoverageTests:IDisposable
{
    private readonly string _path=Path.Combine(Path.GetTempPath(),"meili-coverage-"+Guid.NewGuid().ToString("N"));
    private AutomationService Service(FakeMarket? market=null)=>new(new(_path,DataDirectorySource.CommandLine),market??new());
    private static async Task<JsonElement> Run(AutomationService service,string op,object args,bool writes=false)=>(await service.ExecuteAsync(op,WireJson.Element(args),writes)).GetProperty("data");
    [Fact]
    public async Task Chart_indicators_compute_before_pagination_and_preserve_incomplete_bars()
    {
        using var s=Service();
        var result=await Run(s,"get_chart_analysis",new{symbol="AAPL",compareSymbol="MSFT",offset=1,limit=1,indicators=new{maPeriod=2,bollPeriod=2,kdjPeriod=1}});
        var row=Assert.Single(result.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal(100.5,row.GetProperty("sma").GetDouble());
        Assert.Equal(100.5,row.GetProperty("bollinger").GetProperty("middle").GetDouble());
        Assert.NotEqual(JsonValueKind.Null,row.GetProperty("kdj").GetProperty("k").ValueKind);
        Assert.Equal("ok",result.GetProperty("comparison").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null,result.GetProperty("comparison").GetProperty("basisMatches").ValueKind);
        var first=await Run(s,"get_chart_analysis",new{symbol="AAPL",limit=1});
        var missing=first.GetProperty("page").GetProperty("items")[0];Assert.Equal(JsonValueKind.Null,missing.GetProperty("high").ValueKind);Assert.Equal(JsonValueKind.Null,missing.GetProperty("kdj").GetProperty("k").ValueKind);
    }
    [Fact]
    public async Task Enrichment_matches_shared_analytics_and_history_is_only_recorded_by_explicit_write()
    {
        var at=new DateTimeOffset(2026,11,13,15,0,0,TimeSpan.Zero);var expiry=new DateOnly(2027,6,18);
        var contract=new OptionContract{Underlying="AAPL",ContractSymbol="call",Right=CRight.Call,Strike=100,Expiration=expiry,ImpliedVolatility=.25,Bid=4,Ask=6,OpenInterest=200};
        var market=new FakeMarket{ChainOverride=new(){Underlying="AAPL",UnderlyingPrice=100,Source="fixture",AsOf=at,Expirations=[expiry],Contracts=[contract]}};
        var settings=new SettingsStore(_path);settings.Update(p=>{p.Pricing.RateMode=RiskFreeRateMode.Fixed;p.Pricing.FixedRatePercent=0;});
        using var s=Service(market);
        var result=await Run(s,"analyze_option_chain",new{symbol="AAPL",valuationTime=at});var row=result.GetProperty("page").GetProperty("items")[0];
        var expected=OptionEnricher.Enrich(Meili.Analytics.OptionRight.Call,100,100,expiry,.25,4,6,null,settings:new(){Now=at,RiskFreeRate=0,DividendYield=0});
        Assert.Equal(expected.Delta!.Value,row.GetProperty("enrichment").GetProperty("delta").GetDouble(),10);
        Assert.Equal(2,row.GetProperty("spread").GetDouble());Assert.True(row.GetProperty("isWideSpread").GetBoolean());Assert.Equal(1,row.GetProperty("oiRatio").GetDouble());
        var db=new MarketDb(Path.Combine(_path,"cache","market.db"));
        var insight=await Run(s,"get_volatility_insights",new{symbol="AAPL",valuationTime=at});
        Assert.Equal("insufficient_history",insight.GetProperty("rankStatus").GetString());Assert.Empty(db.LoadIvSamples("AAPL",at.AddYears(-1)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Run(s,"record_option_snapshot",new{symbol="AAPL"}));
        await Run(s,"record_option_snapshot",new{symbol="AAPL",expiry},true);Assert.Equal(expiry,market.LastChainExpiry);Assert.Single(db.LoadIvSamples("AAPL",at.AddYears(-1)));
        for(var i=1;i<=20;i++)db.AddIvSample("AAPL",at.AddDays(-i),"AtmNearest30",.10+i*.001,"fixture");
        insight=await Run(s,"get_volatility_insights",new{symbol="AAPL",valuationTime=at});Assert.Equal("ok",insight.GetProperty("rankStatus").GetString());Assert.Equal(20,insight.GetProperty("rank").GetProperty("days").GetInt32());
        Assert.Equal("fixture",insight.GetProperty("term")[0].GetProperty("source").GetString());
    }
    [Fact]
    public async Task Every_listed_strategy_preset_can_build_from_sufficient_near_and_far_quotes()
    {
        using var s=Service();var listed=await Run(s,"list_strategy_presets",new{});
        var quotes=Enumerable.Range(16,9).SelectMany(i=>new[]{"Call","Put"}.Select(right=>new{right,strike=i*5,premium=3,iv=.25})).ToArray();
        foreach(var row in listed.EnumerateArray())
        {
            var kind=row.GetProperty("kind").GetString();
            var result=await Run(s,"build_strategy",new{kind,spot=100,expiry="2027-06-18",farExpiry="2027-09-17",quotes,farQuotes=quotes,wingSteps=1,quantity=2});
            Assert.Equal("ok",result.GetProperty("status").GetString());Assert.False(result.GetProperty("isExecutable").GetBoolean());
            Assert.NotEmpty(result.GetProperty("legs").EnumerateArray());
            await Run(s,"save_strategy",new{name="generated",symbol="AAPL",kind,legs=result.GetProperty("legs")},true);
        }
    }
    [Fact]
    public async Task Future_what_if_returns_per_leg_model_values_and_rejects_ambiguous_mixed_expiry()
    {
        using var s=Service();var at=new DateTimeOffset(2026,11,13,15,0,0,TimeSpan.Zero);
        var legs=new[]{new LegInput(LegType.Call,PositionSide.Long,1,3,100,new(2027,6,18),.25)};
        var result=await Run(s,"strategy_what_if",new{spot=110,legs,valuationTime=at,horizonDays=7,volatilityShift=.05});
        var expected=WhatIf.Capture(new OptionStrategy(legs.Select(l=>l.ToLeg())),110,new StrategyValuation(at.AddDays(7),0){VolatilityShift=.05});
        Assert.Equal(expected.CombinationPnl,result.GetProperty("result").GetProperty("combinationPnl").GetDouble(),8);
        var mixed=legs.Append(new(LegType.Call,PositionSide.Short,1,1,110,new(2027,9,17),.25)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(()=>Run(s,"strategy_what_if",new{spot=110,legs=mixed}));
    }
    [Fact]
    public async Task Preference_patch_preserves_nested_values_and_never_returns_or_accepts_proxy_urls()
    {
        var store=new SettingsStore(_path);store.Update(s=>{s.Network.ManualProxy="http://private.invalid:7890";s.Charts.Indicators=new(){RsiPeriod=22};});
        using var s=Service();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Run(s,"update_preferences",new{language="en-US"}));
        var changed=await Run(s,"update_preferences",new{charts=new{indicators=new{maPeriod=9}},upDownColors="GreenUp"},true);
        Assert.Equal(22,changed.GetProperty("charts").GetProperty("indicators").GetProperty("rsiPeriod").GetInt32());Assert.True(changed.GetProperty("upDownColorsChosen").GetBoolean());Assert.DoesNotContain("private.invalid",changed.GetRawText());
        var reloaded=new SettingsStore(_path).Load();Assert.Equal("http://private.invalid:7890",reloaded.Network.ManualProxy);
        await Assert.ThrowsAsync<ArgumentException>(()=>Run(s,"update_preferences",new{network=new{manualProxy="http://other.invalid"}},true));
        await Assert.ThrowsAsync<ArgumentException>(()=>Run(s,"update_preferences",new{charts=new{indicators=new{macdFast=30,macdSlow=12}}},true));
    }
    [Fact]
    public void Stale_drawing_writer_reports_conflict_without_mutating_its_snapshot()
    {
        var first=new ChartDrawingStore(_path);var stale=new ChartDrawingStore(_path);
        first.Add("AAPL",new(DrawingKind.HorizontalLine,100));
        Assert.Throws<DataConflictException>(()=>stale.Add("MSFT",new(DrawingKind.HorizontalLine,200)));
        Assert.Empty(stale.For("MSFT"));Assert.Single(new ChartDrawingStore(_path).For("AAPL"));
    }
    [Fact]
    public async Task Drawing_edits_require_writes_and_reject_stale_revisions()
    {
        using var service=Service();
        var added=await Run(service,"add_chart_drawing",new{symbol="AAPL",kind="HorizontalLine",price1=100},true);
        var revision=added.GetProperty("revision").GetString()!;
        var edit=new{symbol="AAPL",index=0,revision,kind="HorizontalLine",price1=110};
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Run(service,"update_chart_drawing",edit));
        var changed=await Run(service,"update_chart_drawing",edit,true);
        Assert.Equal(110,changed.GetProperty("drawings")[0].GetProperty("price1").GetDouble());
        await Assert.ThrowsAsync<DataConflictException>(()=>Run(service,"delete_chart_drawing",new{symbol="AAPL",index=0,revision},true));
        var removed=await Run(service,"delete_chart_drawing",new{symbol="AAPL",index=0,revision=changed.GetProperty("revision").GetString()},true);
        Assert.Empty(removed.GetProperty("drawings").EnumerateArray());
    }
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(_path))Directory.Delete(_path,true);}
}
