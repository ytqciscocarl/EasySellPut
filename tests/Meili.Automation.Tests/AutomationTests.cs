using System.Text.Json;
using Meili.Automation;
using Meili.Core.Abstractions;
using Meili.Core.Alerts;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Shared.Services;

namespace Meili.Automation.Tests;

public sealed class AutomationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "easysellput-automation-" + Guid.NewGuid().ToString("N"));
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
    private AutomationService Service(IMarketDataService? data = null) => new(new(_path, DataDirectorySource.CommandLine), data);
    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
    private static async Task<JsonElement> Run(AutomationService s, string op, string json = "{}", bool writes = true) =>
        (await s.ExecuteAsync(op, J(json), writes)).GetProperty("data");

    [Fact]
    public async Task All_market_operations_preserve_real_metadata_and_paginate()
    {
        using var s = Service(new FakeMarket());
        Assert.Equal("AAPL", (await Run(s, "search_symbols", "{\"query\":\"apple\"}"))[0].GetProperty("symbol").GetString());
        var q = await Run(s, "get_quotes", "{\"symbols\":[\"aapl\",\"MSFT\"]}");
        Assert.Equal("fixture", q.GetProperty("quotes")[0].GetProperty("source").GetString());
        Assert.Equal("MSFT", q.GetProperty("missingSymbols")[0].GetString());
        var bars = await Run(s, "get_bars", "{\"symbol\":\"AAPL\",\"limit\":1}");
        Assert.True(bars.GetProperty("offlineCache").GetBoolean());
        Assert.Equal(JsonValueKind.Null, bars.GetProperty("page").GetProperty("items")[0].GetProperty("open").ValueKind);
        Assert.Equal(1, bars.GetProperty("page").GetProperty("nextOffset").GetInt32());
        var chain = await Run(s, "get_option_chain", "{\"symbol\":\"AAPL\",\"right\":\"put\"}");
        Assert.Equal(1, chain.GetProperty("page").GetProperty("total").GetInt32());
        foreach (var op in new[] { "get_expirations", "get_fundamentals", "get_financials", "get_news" }) await Run(s, op, "{\"symbol\":\"AAPL\"}");
    }
    [Fact]
    public async Task Pricing_and_indicators_reuse_real_algorithms_and_encode_missing_values()
    {
        using var s = Service();
        var p = await Run(s, "calculate_option", """{"right":"call","spot":100,"strike":100,"years":1,"rate":0.05,"volatility":0.2,"price":10.450583572185565}""");
        Assert.InRange(p.GetProperty("greeks").GetProperty("price").GetDouble(), 10.45058, 10.45059);
        Assert.InRange(p.GetProperty("impliedVolatility").GetDouble(), 0.199999, 0.200001);
        var i = await Run(s, "calculate_indicators", """{"closes":[1,2,3,4],"period":3}""");
        Assert.Equal(JsonValueKind.Null, i.GetProperty("sma")[0].ValueKind);
        Assert.Equal(2, i.GetProperty("sma")[2].GetDouble());
    }
    [Fact]
    public async Task Strategy_reports_unboundedness_and_mixed_expiry_semantics()
    {
        using var s = Service();
        var result = await Run(s, "analyze_strategy", """{"spot":100,"legs":[{"type":"Call","side":"Short","quantity":1,"strike":110,"premium":2,"expiry":"2027-06-18"}]}""");
        Assert.True(result.GetProperty("payoff").GetProperty("isMaxLossUnbounded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("payoff").GetProperty("maxLoss").ValueKind);
        await Assert.ThrowsAsync<ArgumentException>(() => Run(s, "analyze_strategy", """{"spot":100,"legs":[{"type":"Call","side":"Long","quantity":1,"strike":100,"premium":2,"expiry":"2027-06-18"},{"type":"Call","side":"Short","quantity":1,"strike":100,"premium":2,"expiry":"2027-07-16"}]}"""));
    }
    [Fact]
    public async Task Local_mutations_round_trip_and_read_only_mode_blocks_every_writer()
    {
        using var s = Service();
        foreach (var op in OperationCatalog.All.Where(x => x.Writes))
        {
            // Permission is checked after valid schema; build minimally schema-valid arguments.
            var properties = op.Schema.GetProperty("properties"); var values = new Dictionary<string, object?>();
            foreach (var req in op.Schema.GetProperty("required").EnumerateArray())
            {
                var name = req.GetString()!; var f = properties.GetProperty(name);
                values[name] = f.TryGetProperty("enum", out var choices) ? choices[0].GetString() : f.GetProperty("type").GetString() switch { "string" => "AAPL", "boolean" => true, "number" => 100.0, "array" => new[] { new { type = "Stock", side = "Long", quantity = 1, premium = 100 } }, _ => 1 };
            }
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.ExecuteAsync(op.Name, WireJson.Element(values), false));
        }
        var group = (await Run(s, "create_watchlist_group", """{"name":"Automation"}""")).GetProperty("group").GetProperty("id").GetString();
        await Run(s, "add_watchlist_symbol", JsonSerializer.Serialize(new { groupId = group, symbol = "aapl" }));
        Assert.Contains("AAPL", (await Run(s, "get_watchlists")).EnumerateArray().First(g => g.GetProperty("id").GetString() == group).GetProperty("symbols").EnumerateArray().Select(x => x.GetString()));
        await Run(s, "save_strategy", """{"name":"covered","symbol":"AAPL","legs":[{"type":"Stock","side":"Long","quantity":100,"premium":100}]}""");
        Assert.Single((await Run(s, "get_saved_strategies")).EnumerateArray());
        await Run(s, "delete_strategy", """{"name":"covered"}""");
        Assert.Empty((await Run(s, "get_saved_strategies")).EnumerateArray());
        var alert = await Run(s, "save_alert", """{"symbol":"AAPL","kind":"PriceAbove","threshold":120}""");
        var id = alert.GetProperty("id").GetString();
        await Run(s, "set_alert_enabled", JsonSerializer.Serialize(new { id, enabled = false }));
        Assert.False((await Run(s, "get_alerts"))[0].GetProperty("enabled").GetBoolean());
        await Run(s, "delete_alert", JsonSerializer.Serialize(new { id }));
        Assert.Empty((await Run(s, "get_alerts")).EnumerateArray());
    }
    [Fact]
    public async Task Arguments_cancellation_and_corrupt_local_data_fail_explicitly()
    {
        using var s = Service(new FakeMarket());
        await Assert.ThrowsAsync<ArgumentException>(() => Run(s, "get_quotes", """{"symbols":[],"invented":1}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(s, "get_bars", """{"symbol":"AAPL","from":"2027-01-01"}"""));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.ExecuteAsync("get_settings", J("{}"), false, cancelled.Token));
        Directory.CreateDirectory(_path); File.WriteAllText(Path.Combine(_path, "settings.json"), "broken");
        await Assert.ThrowsAnyAsync<JsonException>(() => Run(s, "get_watchlists"));
        Assert.Equal("broken", File.ReadAllText(Path.Combine(_path, "settings.json")));
    }
    [Fact]
    public async Task Inflight_network_cancellation_releases_the_host_for_next_request()
    {
        using var service = Service(new FakeMarket { DelayQuotes = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync("get_quotes", J("{\"symbols\":[\"AAPL\"]}"), false, timeout.Token));
        Assert.Equal(_path, (await Run(service, "get_settings")).GetProperty("dataDirectory").GetProperty("path").GetString());
    }
    [Fact]
    public async Task Bad_pagination_and_dates_are_rejected_before_accessing_sources()
    {
        using var s = Service(new FakeMarket { DelayQuotes = true });
        await Assert.ThrowsAsync<ArgumentException>(() => Run(s, "get_bars", """{"symbol":"AAPL","limit":0}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(s, "get_option_chain", """{"symbol":"AAPL","offset":-1}"""));
        await Assert.ThrowsAnyAsync<JsonException>(() =>
        {
            Directory.CreateDirectory(_path); File.WriteAllText(Path.Combine(_path, "settings.json"), "[]");
            return Run(s, "get_settings");
        });
    }
    [Fact]
    public void Structured_errors_do_not_leak_proxy_credentials_or_provider_bodies()
    {
        var error = WireJson.Element(WireJson.Error(new Meili.Data.Routing.AllSourcesFailedException(DataCapability.Quote, [("yahoo", "https://user:secret@example.com response payload")])));
        Assert.Equal("data_source_error", error.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("secret", error.GetRawText());
    }
    [Fact]
    public void Stale_settings_alerts_and_strategies_cannot_overwrite_another_writer()
    {
        var a = new SettingsStore(_path); var b = new SettingsStore(_path); a.Load(); b.Load();
        a.Update(s => s.Language = "en-US"); Assert.Throws<DataConflictException>(() => b.Update(s => s.Language = "zh-CN"));
        Assert.Equal("en-US", new SettingsStore(_path).Load().Language);
        var x = new AlertStore(_path); var y = new AlertStore(_path); x.Load(); y.Load();
        x.Upsert(new() { Symbol = "AAPL", Threshold = 100 }); Assert.Throws<DataConflictException>(() => y.Upsert(new() { Symbol = "MSFT", Threshold = 200 }));
        var one = new StrategyStore(_path); var two = new StrategyStore(_path);
        one.Save(new("one", "AAPL", Meili.Analytics.StrategyKind.Custom, [], DateTimeOffset.UtcNow));
        Assert.Throws<DataConflictException>(() => two.Save(new("two", "MSFT", Meili.Analytics.StrategyKind.Custom, [], DateTimeOffset.UtcNow)));
        b.Load(); b.Update(s => s.Theme = AppThemeMode.Dark); Assert.Equal("en-US", new SettingsStore(_path).Load().Language);
    }
}
