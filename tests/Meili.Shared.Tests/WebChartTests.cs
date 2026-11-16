using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Tests;

[Collection(nameof(UiPlatformCollection))]
public sealed class WebChartTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "meili-webchart-" + Guid.NewGuid().ToString("N"));
    private readonly ChartsViewModel _vm;
    private readonly ChartPalette _palette;
    public WebChartTests()
    {
        Ui.Platform = new FakeUiPlatform();
        var loc = new LocalizationService();
        var settings = new SettingsStore(_directory);
        var start = new DateTimeOffset(2025, 1, 6, 14, 30, 0, TimeSpan.Zero);
        var data = new PricingSnapshotTests.SnapshotData { Bars = new BarSeries
        {
            Symbol = "AAPL", Interval = BarInterval.Minute5, Source = "fixture",
            Bars = Enumerable.Range(0, 120).Select(i => i == 70 ? Bar.CloseOnly(start.AddMinutes(i * 5), 100 + i, 1000)
                : Bar.FromSource(start.AddMinutes(i * 5), 100 + i, 101 + i, 99 + i, 100.5 + i, 1000)).ToArray(),
        }};
        _vm = new ChartsViewModel(new WatchlistStore(settings), data, loc, new SymbolContext(), settings, new ChartDrawingStore(_directory));
        _palette = ChartPalette.Create(true, new(204,142,138), new(110,170,138), new(201,198,193), new(150,145,136), ThemePalette.Dark);
    }
    [Fact]
    public async Task Daily_bars_do_not_receive_intraday_session_labels()
    {
        await _vm.LoadAsync();
        _vm.Series=new BarSeries{Symbol="AAPL",Interval=BarInterval.Day1,Source="fixture",Bars=_vm.Series!.Bars};
        using var json=JsonDocument.Parse(WebChartSnapshot.Serialize(_vm,_palette));
        Assert.False(json.RootElement.GetProperty("intraday").GetBoolean());
        Assert.All(json.RootElement.GetProperty("rows").EnumerateArray(),r=>Assert.Equal(JsonValueKind.Null,r.GetProperty("session").ValueKind));
    }
    [Fact]
    public async Task Snapshot_preserves_real_prices_indicator_gaps_and_the_same_analytics_values()
    {
        await _vm.LoadAsync();
        using var json = JsonDocument.Parse(WebChartSnapshot.Serialize(_vm, _palette));
        var rows = json.RootElement.GetProperty("rows");
        Assert.Equal(120, rows.GetArrayLength());
        Assert.Equal("Regular", rows[0].GetProperty("session").GetString());
        Assert.Equal("AfterHours", rows[119].GetProperty("session").GetString());
        Assert.Equal(JsonValueKind.Null, rows[70].GetProperty("open").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[70].GetProperty("high").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[70].GetProperty("k").ValueKind);
        Assert.Equal(170, rows[70].GetProperty("close").GetDouble());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("ma").ValueKind);
        Assert.Equal(_vm.Snapshot!.Sma[^1], rows[119].GetProperty("ma").GetDouble());
        Assert.Equal(_vm.Snapshot.Macd.Macd[^1], rows[119].GetProperty("macd").GetDouble());
        Assert.Contains("fixture", json.RootElement.GetProperty("key").GetString());
        var theme = json.RootElement.GetProperty("theme");
        Assert.True(theme.GetProperty("dark").GetBoolean());
        Assert.Equal("#30302E", theme.GetProperty("control").GetString());
        Assert.Equal("#4D4C48", theme.GetProperty("border").GetString());
        Assert.Equal("#FAF9F51F", theme.GetProperty("grid").GetString());
        Assert.Equal("#FAF9F5", theme.GetProperty("accent").GetString());
        Assert.Equal("#3A2A28", theme.GetProperty("errorBackground").GetString());
    }
    [Fact]
    public async Task Private_channel_serves_bundled_assets_versions_and_rejects_foreign_origins_and_untyped_events()
    {
        await _vm.LoadAsync();
        using var session = new WebChartSession(_vm); session.Update(_palette);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var root = new Uri(session.PageUri, ".");
        using var page = await client.GetAsync(session.PageUri);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("TradingView", await page.Content.ReadAsStringAsync());
        using var state = JsonDocument.Parse(await client.GetStringAsync(new Uri(root, "state")));
        var version = state.RootElement.GetProperty("version").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(new Uri(root, "state?after=" + version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri(root, "../state"))).StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "event")) { Content = new StringContent("{\"kind\":\"attribution\"}", Encoding.UTF8, "application/json") };
        foreign.Headers.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        using var invalid = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "event")) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"kind\":\"shell\",\"command\":\"anything\"}")) };
        invalid.Content.Headers.ContentType = new("application/json"); invalid.Headers.Add("Origin", session.Origin);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invalid)).StatusCode);
        Assert.True(session.IsPageUrl(session.PageUri.AbsoluteUri));
        Assert.False(session.IsPageUrl("https://untrusted.example/"));
        Assert.False(session.IsPageUrl(new Uri(root, "state").AbsoluteUri));
    }
    [Fact]
    public async Task Only_current_chart_typed_drawing_gestures_reach_existing_drawing_store()
    {
        await _vm.LoadAsync();
        using var session = new WebChartSession(_vm); session.Update(_palette);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var state = JsonDocument.Parse(WebChartSnapshot.Serialize(_vm, _palette));
        var key = state.RootElement.GetProperty("key").GetString();
        _vm.StartDrawingCommand.Execute(DrawingKind.HorizontalLine);
        async Task Send(string? chartKey, int index)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(session.PageUri, "event"))
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { kind = "drawing", key = chartKey, index, price = 123.45 })) };
            request.Headers.Add("Origin", session.Origin); request.Content.Headers.ContentType = new("application/json");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);
        }
        await Send("old-symbol", 2); await Send(key, 999);
        Assert.Empty(_vm.Drawings);
        await Send(key, 2);
        Assert.Equal(123.45, Assert.Single(_vm.Drawings).Price1);
    }
    [Fact]
    public async Task Reload_cancels_the_old_draft_and_old_tool_epoch_cannot_cancel_a_new_tool()
    {
        await _vm.LoadAsync(); _vm.StartDrawingCommand.Execute(DrawingKind.TrendLine);
        _vm.AddDrawingPoint(_vm.Series!.Bars[10].Time,110);Assert.NotNull(_vm.DrawingStart);
        var oldEpoch=_vm.DrawingEpoch;await _vm.LoadAsync();Assert.Null(_vm.DrawingStart);Assert.Null(_vm.DrawMode);Assert.True(_vm.DrawingEpoch>oldEpoch);
        using var session=new WebChartSession(_vm);session.Update(_palette);
        using var json=JsonDocument.Parse(WebChartSnapshot.Serialize(_vm,_palette));var key=json.RootElement.GetProperty("key").GetString();
        _vm.StartDrawingCommand.Execute(DrawingKind.HorizontalLine);
        using var client=new HttpClient(new HttpClientHandler{UseProxy=false});
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(session.PageUri,"event")){Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new{kind="drawing-cancel",key,epoch=oldEpoch,mode="HorizontalLine"}))};
        request.Content.Headers.ContentType=new("application/json");
        request.Headers.Add("Origin",session.Origin);Assert.Equal(HttpStatusCode.NoContent,(await client.SendAsync(request)).StatusCode);
        Assert.Equal(DrawingKind.HorizontalLine,_vm.DrawMode);Assert.Empty(_vm.Drawings);
        // The UI model can switch range before the next serialized WebView snapshot is published.
        _vm.Range=ChartRangeKind.Minute5;
        using var staleRequest=new HttpRequestMessage(HttpMethod.Post,new Uri(session.PageUri,"event")){Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new{kind="drawing",key,index=10,price=110}))};
        staleRequest.Content.Headers.ContentType=new("application/json");staleRequest.Headers.Add("Origin",session.Origin);
        Assert.Equal(HttpStatusCode.NoContent,(await client.SendAsync(staleRequest)).StatusCode);Assert.Empty(_vm.Drawings);

    }
    public void Dispose() { _vm.Deactivate(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
