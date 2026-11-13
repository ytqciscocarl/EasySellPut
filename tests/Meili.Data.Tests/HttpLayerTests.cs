using System.Net;
using Meili.Core.Settings;
using Meili.Data.Http;
using Meili.Data.Json;
using Meili.Data.Providers;
using Meili.Core.Models;

namespace Meili.Data.Tests;

public class HttpLayerTests
{
    [Fact]
    public async Task Retries_transient_errors_then_succeeds()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadGateway).Throw(new HttpRequestException("SSL UNEXPECTED_EOF")).Enqueue(HttpStatusCode.OK, "{\"ok\":1}");
        var gw = TestHttp.Gateway(h, retries: 2);
        var events = new List<RequestCompleted>();
        gw.Completed += events.Add;
        var p = await gw.GetAsync("t", "https://example.test/a");
        Assert.Equal(200, p.StatusCode);
        Assert.Equal(3, h.Requests.Count);
        Assert.Equal([false, false, true], events.Select(e => e.Success));
    }

    [Fact]
    public async Task Does_not_retry_client_errors()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.NotFound).Enqueue(HttpStatusCode.OK);
        var ex = await Assert.ThrowsAsync<DataSourceException>(() => TestHttp.Gateway(h).GetAsync("t", "https://example.test/a"));
        Assert.Equal(DataErrorKind.Http, ex.Kind);
        Assert.Equal(404, ex.StatusCode);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Forbidden_is_reported_as_blocked()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<DataSourceException>(() => TestHttp.Gateway(h).GetAsync("t", "https://example.test/a"));
        Assert.Equal(DataErrorKind.Blocked, ex.Kind);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Gives_up_after_max_retries()
    {
        var h = new FakeHandler { Fallback = _ => FakeHandler.Resp(HttpStatusCode.ServiceUnavailable, "") };
        await Assert.ThrowsAsync<DataSourceException>(() => TestHttp.Gateway(h, retries: 1).GetAsync("t", "https://example.test/a"));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Custom_headers_replace_defaults()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK);
        await TestHttp.Gateway(h).GetAsync("t", "https://example.test/a", new Dictionary<string, string> { ["Referer"] = "https://finance.sina.com.cn/", ["Accept"] = "text/plain" });
        var r = h.Requests[0];
        Assert.Equal("https://finance.sina.com.cn/", r.Headers.Referrer!.ToString());
        Assert.Equal("text/plain", Assert.Single(r.Headers.Accept).MediaType);
    }

    [Fact]
    public async Task Rate_limiter_spaces_requests()
    {
        var rl = new RateLimiter();
        rl.Configure("x", TimeSpan.FromMilliseconds(120));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) await rl.WaitAsync("x", CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 220, $"elapsed {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Cache_deduplicates_concurrent_requests_and_honours_ttl()
    {
        var cache = new ResponseCache();
        var calls = 0;
        async Task<string> Factory(CancellationToken _) { Interlocked.Increment(ref calls); await Task.Delay(50); return "v"; }
        var all = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory)));
        Assert.All(all, v => Assert.Equal("v", v));
        Assert.Equal(1, calls);
        await cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory);
        Assert.Equal(1, calls);
        cache.Remove("k");
        await cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cache_shared_request_survives_the_first_caller_cancelling()
    {
        var cache = new ResponseCache();
        var ttl = TimeSpan.FromMinutes(1);
        var calls = 0;
        var tokens = new List<CancellationToken>();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Factory(CancellationToken t) { Interlocked.Increment(ref calls); tokens.Add(t); return gate.Task; }

        using var cts = new CancellationTokenSource();
        var first = cache.GetOrAddAsync("k", ttl, Factory, cts.Token);
        var second = cache.GetOrAddAsync("k", ttl, Factory);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));

        // the cancelled caller left the in-flight request alone: a third caller still joins it
        var third = cache.GetOrAddAsync("k", ttl, Factory);
        gate.SetResult("v");
        Assert.Equal("v", await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("v", await third.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, calls);
        Assert.All(tokens, t => Assert.False(t.CanBeCanceled)); // the shared work never sees a caller's token

        Assert.Equal("v", await cache.GetOrAddAsync("k", ttl, Factory)); // inside the TTL: served from cache
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cache_abandoned_request_that_fails_later_raises_no_unobserved_exception()
    {
        var marker = new InvalidOperationException("abandoned shared request failed");
        var seen = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => ReferenceEquals(x, marker))) seen.Add(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await AbandonThenFailAsync(marker);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.Empty(seen);
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    /// <summary>The only caller cancels, then the shared factory throws. Kept out of line so nothing here keeps the task alive.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AbandonThenFailAsync(Exception boom)
    {
        var cache = new ResponseCache();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Factory(CancellationToken _)
        {
            try { return await gate.Task; }
            finally { failed.TrySetResult(); }
        }
        using var cts = new CancellationTokenSource();
        var only = cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only.WaitAsync(TimeSpan.FromSeconds(10)));
        gate.SetException(boom);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100); // let the shared task finish faulting
    }

    [Fact]
    public async Task Cache_observing_the_shared_fault_still_delivers_it_to_callers_who_await()
    {
        var cache = new ResponseCache();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Factory(CancellationToken _) => gate.Task;
        using var cts = new CancellationTokenSource();
        var cancelled = cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory, cts.Token);
        var waiting = cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10)));
        var boom = new InvalidOperationException("boom");
        gate.SetException(boom);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(boom, ex);
    }

    [Fact]
    public async Task Cache_failed_request_is_not_cached_and_the_next_caller_retries()
    {
        var cache = new ResponseCache();
        var calls = 0;
        Task<string> Factory(CancellationToken _) =>
            ++calls == 1 ? throw new InvalidOperationException("boom") : Task.FromResult("v");
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory));
        Assert.Equal("v", await cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cache_failed_async_request_is_removed_from_flight_so_the_next_caller_retries()
    {
        var cache = new ResponseCache();
        var calls = 0;
        async Task<string> Factory(CancellationToken _)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
            return "v";
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory));
        Assert.Equal("v", await cache.GetOrAddAsync("k", TimeSpan.FromMinutes(1), Factory));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("127.0.0.1:45444", "http://127.0.0.1:45444/")]
    [InlineData("http=127.0.0.1:7890;https=127.0.0.1:7891", "http://127.0.0.1:7891/")]
    [InlineData("socks=127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    public void Parses_windows_proxy_setting(string value, string expected)
    {
        Assert.True(ProxyDetector.TryParseWindowsProxyServer(value, out var u));
        Assert.Equal(expected, u.ToString());
    }

    private const string ScutilClash = """
        <dictionary> {
          ExceptionsList : <array> {
            0 : 127.0.0.1
            1 : localhost
          }
          FTPPassive : 1
          HTTPEnable : 1
          HTTPPort : 7890
          HTTPProxy : 127.0.0.1
          HTTPSEnable : 1
          HTTPSPort : 7897
          HTTPSProxy : 127.0.0.1
          SOCKSEnable : 1
          SOCKSPort : 7891
          SOCKSProxy : 127.0.0.1
        }
        """;

    [Fact]
    public void Parses_macos_scutil_proxy_preferring_https()
    {
        Assert.True(ProxyDetector.TryParseScutilProxy(ScutilClash, out var u));
        Assert.Equal("http://127.0.0.1:7897/", u.ToString());
    }

    [Fact]
    public void Parses_macos_scutil_socks_only_and_ignores_empty()
    {
        Assert.True(ProxyDetector.TryParseScutilProxy("<dictionary> {\n  SOCKSEnable : 0\n  SOCKSPort : 1080\n  SOCKSProxy : 127.0.0.1\n}", out var u));
        Assert.Equal("socks5://127.0.0.1:1080/", u.ToString());
        Assert.False(ProxyDetector.TryParseScutilProxy("<dictionary> {\n  HTTPEnable : 0\n}", out _));
    }

    [Fact]
    public void Macos_proxy_comes_first_and_only_when_loopback()
    {
        var first = ProxyDetector.Candidates(ScutilClash).First();
        Assert.Equal(("http://127.0.0.1:7897/", "macOS proxy setting"), (first.Uri.ToString(), first.Origin));
        var remote = ScutilClash.Replace("127.0.0.1", "10.0.0.5");
        Assert.DoesNotContain(ProxyDetector.Candidates(remote), c => c.Origin == "macOS proxy setting");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("ftp://1.2.3.4:21", false)]
    [InlineData("socks5://127.0.0.1:7891", true)]
    [InlineData("127.0.0.1:7890", true)]
    public void Validates_manual_proxy(string text, bool ok) => Assert.Equal(ok, ProxyDetector.TryParseProxy(text, out _));

    [Fact]
    public async Task Explicit_modes_resolve_without_probing()
    {
        Assert.Null((await ProxyDetector.ResolveAsync(new NetworkSettings { ProxyMode = ProxyMode.Direct })).ProxyUri);
        var m = await ProxyDetector.ResolveAsync(new NetworkSettings { ProxyMode = ProxyMode.Manual, ManualProxy = "127.0.0.1:45444" });
        Assert.Equal(new Uri("http://127.0.0.1:45444"), m.ProxyUri);
        Assert.True((await ProxyDetector.ResolveAsync(new NetworkSettings { ProxyMode = ProxyMode.System })).UseSystemProxy);
    }

    [Theory]
    [InlineData("$1,234.50", 1234.5)]
    [InlineData("+2.43%", 2.43)]
    [InlineData("-0.05", -0.05)]
    [InlineData("5.168T", 5.168e12)]
    [InlineData("34,453,477", 34453477)]
    public void Parses_messy_numbers(string s, double expected) => Assert.Equal(expected, JsonX.ParseNumber(s)!.Value, 6);

    [Theory]
    [InlineData("N/A")]
    [InlineData("--")]
    [InlineData("")]
    public void Messy_placeholders_are_null(string s) => Assert.Null(JsonX.ParseNumber(s));

    [Fact]
    public void Occ_symbol_round_trips()
    {
        Assert.True(OccSymbol.TryParse("AAPL261120C00355000", out var root, out var exp, out var right, out var k));
        Assert.Equal(("AAPL", new DateOnly(2026, 11, 20), OptionRight.Call, 355.0), (root, exp, right, k));
        Assert.Equal("AAPL261120C00355000", OccSymbol.Format(root, exp, right, k));
        Assert.True(OccSymbol.TryParse("SPXW 261231P05500500", out root, out _, out right, out k));
        Assert.Equal(("SPXW", OptionRight.Put, 5500.5), (root, right, k));
    }
}