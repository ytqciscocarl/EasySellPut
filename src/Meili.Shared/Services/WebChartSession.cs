using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meili.Core.Diagnostics;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Services;

/// <summary>
/// A private, bounded loopback channel for bundled chart assets/data and typed chart gestures.
/// No JavaScript/native bridge, remote content, filesystem paths, shell commands, or market-data fetch APIs.
/// </summary>
public sealed class WebChartSession : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _clients = new(8);
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly ChartsViewModel? _vm;
    private readonly StrategyViewModel? _strategy;
    private State _state = new(0, "{}");
    private long _revision;
    private bool _disposed;
    private ChartPalette? _lastPalette;
    public bool IsStrategyWorkbench => _strategy is not null;
    public Uri PageUri { get; }
    public string Origin => PageUri.GetLeftPart(UriPartial.Authority);
    public event Action<JsonElement>? DiagnosticReceived;
    private sealed record State(long Version, string Json);

    public WebChartSession(ChartsViewModel vm) : this(vm, null) { }
    public WebChartSession(StrategyViewModel vm) : this(null, vm) { }
    private WebChartSession(ChartsViewModel? vm, StrategyViewModel? strategy)
    {
        _vm = vm; _strategy = strategy;
        DesignMotion.Changed+=OnMotionChanged;
        _listener.Start();
        PageUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{_token}/index.html");
        _ = AcceptAsync();
    }
    public bool IsPageUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.GetLeftPart(UriPartial.Authority) == Origin && uri.AbsolutePath == PageUri.AbsolutePath;

    public void Update(ChartPalette palette)
    {
        if (_disposed) return;
        _lastPalette=palette;
        Volatile.Write(ref _state, new State(++_revision, _strategy is { } strategy ? StrategyWorkbench.Serialize(strategy, palette) : WebChartSnapshot.Serialize(_vm!, palette)));
    }

    private void OnMotionChanged(object? sender,EventArgs e){if(_lastPalette is { } palette)Update(palette);}

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _clients.WaitAsync(_stop.Token);
                try { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = HandleAsync(client); }
                catch { _clients.Release(); throw; }
            }
        }
        catch (OperationCanceledException) { } // expected: page/session closed
        catch (SocketException) when (_stop.IsCancellationRequested) { } // expected: listener stopped
        catch (Exception ex) { AppLog.Warn("web-chart", "Chart listener stopped", ex); }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        try
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var ct = deadline.Token;
            using var stream = client.GetStream();
            // Read bytes, not StreamReader chars: HTTP Content-Length is UTF-8 bytes.
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 16384)
            {
                if (await stream.ReadAsync(one, ct) == 0) return;
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            if (header.Count >= 16384) { await Reply(stream, 431, "text/plain", [], ct); return; }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var request = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) { await Reply(stream, 400, "text/plain", [], ct); return; }
            }
            var host = PageUri.Authority;
            if (request.Length != 3 || !headers.TryGetValue("Host", out var sentHost) || sentHost != host
                || !request[1].StartsWith($"/{_token}/", StringComparison.Ordinal)
                || headers.ContainsKey("Transfer-Encoding")
                || (headers.TryGetValue("Origin", out var origin) && origin != Origin)
                || (headers.TryGetValue("Sec-Fetch-Site", out var site) && site == "cross-site"))
            { await Reply(stream, 403, "text/plain", [], ct); return; }
            var route = request[1][(_token.Length + 2)..].Split('?')[0];
            if (request[0] == "GET")
            {
                if (route == "state")
                {
                    var state = Volatile.Read(ref _state);
                    if (request[1].EndsWith("?after=" + state.Version, StringComparison.Ordinal)) { await Reply(stream, 204, "application/json", [], ct); return; }
                    var body = Encoding.UTF8.GetBytes($"{{\"version\":{state.Version},\"data\":{state.Json}}}");
                    await Reply(stream, 200, "application/json", body, ct); return;
                }
                var file = route switch { "index.html" => _strategy is null ? "index.html" : "workbench.html", "workbench.js" when _strategy is not null => "workbench.js", "chart.js" when _strategy is null => "chart.js", "icons.js" => "icons.js", "motion.js" => "motion.js", "design-tokens.css" => "design-tokens.css", "components.css" => "components.css", "fonts/SourceSans3-Regular.ttf" or "fonts/SourceSans3-Semibold.ttf" or "fonts/SourceSerif4-Regular.ttf" or "fonts/SourceCodePro-Regular.ttf" => route, "library.js" => "vendor/lightweight-charts.js", _ => null };
                if (file is not null)
                {
                    await Reply(stream, 200, file.EndsWith(".ttf",StringComparison.Ordinal) ? "font/ttf" : file.EndsWith(".css",StringComparison.Ordinal) ? "text/css; charset=utf-8" : file.EndsWith("html", StringComparison.Ordinal) ? "text/html; charset=utf-8" : "text/javascript; charset=utf-8", Asset(file), ct); return;
                }
            }
            if (request[0] == "POST" && route == "event" && origin == Origin
                && headers.TryGetValue("Content-Type", out var type) && type == "application/json"
                && headers.TryGetValue("Content-Length", out var size) && int.TryParse(size, out var count) && count is > 0 and <= 4096)
            {
                var body = new byte[count]; await stream.ReadExactlyAsync(body, ct);
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                var message = document.RootElement.Clone();
                if (!ValidEvent(message)) { await Reply(stream, 400, "text/plain", [], ct); return; }
                Ui.Platform.Run(() => Dispatch(message));
                await Reply(stream, 204, "application/json", [], ct); return;
            }
            await Reply(stream, 404, "text/plain", [], ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException)
        { /* expected: browser disconnect, malformed bounded JSON, or page shutdown */ }
        catch (Exception ex) { AppLog.Warn("web-chart", "Chart request failed", ex); }
        finally { _clients.Release(); }
    }
    private bool ValidEvent(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
        if (_strategy is not null && kind.GetString() is not ("attribution" or "diagnostic")) return StrategyWorkbench.ValidEvent(e);
        return kind.GetString() switch
        {
            "attribution" => true,
            "open-event" => e.TryGetProperty("key", out var eventKey) && eventKey.ValueKind == JsonValueKind.String && eventKey.GetString()!.Length <= 512
                && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString()!.Length == 64,
            "diagnostic" => e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String,
            "drawing-cancel" => e.TryGetProperty("key", out var cancelKey) && cancelKey.ValueKind == JsonValueKind.String && cancelKey.GetString()!.Length <= 512,
            "drawing-edit" or "drawing-delete" => e.TryGetProperty("key", out var editKey) && editKey.ValueKind == JsonValueKind.String && editKey.GetString()!.Length <= 512
                && e.TryGetProperty("index", out var editIndex) && editIndex.ValueKind == JsonValueKind.Number && editIndex.TryGetInt32(out var number) && number is >= 0 and < 200
                && e.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.String && revision.GetString()!.Length == 64
                && (kind.GetString() == "drawing-delete" || e.TryGetProperty("price1", out var first) && first.ValueKind == JsonValueKind.Number && first.TryGetDouble(out var firstValue) && double.IsFinite(firstValue) && firstValue is >= 0 and <= 1e12 && e.TryGetProperty("price2", out var second) && second.ValueKind == JsonValueKind.Number && second.TryGetDouble(out var secondValue) && double.IsFinite(secondValue) && secondValue is >= 0 and <= 1e12),
            "crosshair" or "drawing" => e.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                && e.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out _)
                && (kind.GetString() != "drawing" || (e.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var value) && double.IsFinite(value) && Math.Abs(value) < 1e15)),
            _ => false,
        };
    }
    private void Dispatch(JsonElement message)
    {
        if (_disposed) return;
        try
        {
            var kind = message.GetProperty("kind").GetString();
            if (kind == "attribution") { Ui.Platform.OpenUrl("https://www.tradingview.com/"); return; }
            if (kind == "diagnostic") { DiagnosticReceived?.Invoke(message); return; }
            if (_strategy is { } strategy) { StrategyWorkbench.Dispatch(strategy, message); return; }
            var current = Volatile.Read(ref _state);
            using var state = JsonDocument.Parse(current.Json);
            if (message.GetProperty("key").GetString() != state.RootElement.GetProperty("key").GetString() || message.GetProperty("key").GetString() != _vm!.ChartKey) return;
            if (kind == "open-event") { _vm!.OpenTimelineEvent(message.GetProperty("id").GetString()!); return; }
            if (kind is "drawing" or "drawing-cancel" && message.TryGetProperty("epoch", out var epoch) && (epoch.ValueKind != JsonValueKind.Number || !epoch.TryGetInt64(out var epochValue) || epochValue != _vm!.DrawingEpoch)) return;
            if (kind == "drawing-cancel") { if (!message.TryGetProperty("mode", out var mode) || mode.ValueKind == JsonValueKind.String && mode.GetString() == _vm!.DrawMode?.ToString()) _vm!.CancelDrawing(); return; }
            if (kind is "drawing-edit" or "drawing-delete")
            {
                _vm!.EditDrawing(message.GetProperty("index").GetInt32(), kind == "drawing-delete" ? null : message.GetProperty("price1").GetDouble(), kind == "drawing-delete" ? null : message.GetProperty("price2").GetDouble(), message.GetProperty("revision").GetString()!); return;
            }
            var index = message.GetProperty("index").GetInt32();
            if (_vm!.Series is not { } series || index < 0 || index >= series.Bars.Count) { if (kind == "crosshair") _vm.UpdateCrosshair(-1); return; }
            if (kind == "drawing" && message.TryGetProperty("mode", out var drawingMode) && (drawingMode.ValueKind != JsonValueKind.String || drawingMode.GetString() != _vm.DrawMode?.ToString())) return;
            if (kind == "drawing") _vm.AddDrawingPoint(series.Bars[index].Time, message.GetProperty("price").GetDouble());
            else _vm.UpdateCrosshair(index);
        }
        catch (Exception ex) { AppLog.Warn("web-chart", "Chart gesture failed", ex); }
    }
    public static byte[] Asset(string name)
    {
        using var source = typeof(WebChartSession).Assembly.GetManifestResourceStream("WebCharts/" + name)
            ?? throw new InvalidOperationException("Bundled chart asset is missing: " + name);
        using var target = new MemoryStream(); source.CopyTo(target); return target.ToArray();
    }
    private static async Task Reply(NetworkStream stream, int status, string type, byte[] body, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nCross-Origin-Resource-Policy: same-origin\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct); if (body.Length > 0) await stream.WriteAsync(body, ct);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; DesignMotion.Changed-=OnMotionChanged; _stop.Cancel(); _listener.Stop();
        // In-flight handlers own cancellation registrations and release the bounded semaphore on exit.
    }
}
