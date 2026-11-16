using System.Security.Cryptography;
using System.Text;
using Meili.Core.Abstractions;
using Meili.Core.Diagnostics;
using Meili.Core.Models;
using Meili.Core.Time;

namespace Meili.Automation;

public sealed record MarketEvent(string Id, string Symbol, string Type, string Title, DateTimeOffset? Published,
    DateOnly? Date, DateOnly? EndDate, string Precision, string Confirmation, string Source, string? Publisher,
    string? Url, string? Summary, DateTimeOffset ObservedAt, DateTimeOffset? ProviderAsOf, DateTimeOffset? ProviderFetchedAt);
public sealed record EventCoverage(string Type, string Status, string? ErrorCode = null);
public sealed record MarketTimeline(string Symbol, IReadOnlyList<MarketEvent> Events, IReadOnlyList<EventCoverage> Coverage,
    string Status, DateTimeOffset ObservedAt, bool Truncated, int UndatedExcluded,
    string Scope = "recent_news_and_current_calendar_snapshot");

/// <summary>Shared desktop/CLI/MCP aggregation. Receipt time is never presented as the publisher's update time.</summary>
public static class MarketTimelineService
{
    public static async Task<MarketTimeline> LoadAsync(IMarketDataService market, string symbol, string type = "all",
        DateOnly? from = null, DateOnly? to = null, int limit = 50, CancellationToken ct = default)
    {
        if (type is not ("all" or "news" or "earnings" or "ex_dividend") || limit is < 1 or > 100 || from > to)
            throw new ArgumentException("Invalid timeline filter or bounds.");
        if (!Meili.Core.Settings.WatchlistStore.TryNormalizeSymbol(symbol, out var normalized, out _)) throw new ArgumentException("Invalid ticker symbol.");
        symbol = normalized;
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var newsTask = type is "all" or "news" ? Read(() => market.GetNewsAsync(symbol, 50, ct), ct) : Task.FromResult((Value: (IReadOnlyList<NewsItem>?)null, Error: (string?)null));
        var fundamentalsTask = type != "news" ? Read(() => market.GetFundamentalsAsync(symbol, ct), ct) : Task.FromResult((Value: (Fundamentals?)null, Error: (string?)null));
        await Task.WhenAll(newsTask, fundamentalsTask);
        var news = await newsTask; var fundamentals = await fundamentalsTask;
        var events = new List<MarketEvent>(); var coverage = new List<EventCoverage>();
        if (type is "all" or "news")
        {
            foreach (var n in (news.Value ?? []).Take(50))
            {
                var date = n.Published is { } time ? DateOnly.FromDateTime(MarketClock.ToEastern(time)) : (DateOnly?)null;
                var safe = IsSafeUrl(n.Url) ? n.Url : null;
                events.Add(new(Id(symbol, "news", n.Source, n.Url, n.Published?.ToString("O"), n.Title), symbol, "news", n.Title,
                    n.Published, date, null, n.Published is null ? "unknown" : "timestamp", n.Published is null ? "unverified" : "reported_published",
                    n.Source, n.Publisher, safe, n.Summary, now, null, null));
            }
            coverage.Add(new("news", news.Error is not null ? "unavailable" : news.Value?.Count > 0 ? "available" : "empty", news.Error));
        }
        foreach (var kind in new[] { "earnings", "ex_dividend" }.Where(k => type == "all" || type == k))
        {
            var f = fundamentals.Value;
            var date = kind == "earnings" ? f?.NextEarningsDate : f?.ExDividendDate;
            var end = kind == "earnings" ? f?.EarningsDateEnd : null;
            var status = kind == "earnings" ? f?.EarningsDateStatus : f?.ExDividendDateStatus;
            if (f is not null && !string.Equals(f.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) { coverage.Add(new(kind, "unavailable", "symbol_mismatch")); continue; }
            if (date is { } day)
                events.Add(new(Id(symbol, kind, f!.Source, day.ToString("yyyy-MM-dd")), symbol, kind, kind,
                    null, day, end is { } e && e >= day ? e : null, end > day ? "date_range" : "date",
                    status switch { EventDateStatus.Estimated => "estimated", EventDateStatus.SourceConfirmed => "source_confirmed", _ => "unverified" },
                    f.Source, null, null, null, now, f.AsOf, f.FetchedAt));
            coverage.Add(new(kind, fundamentals.Error is not null ? "unavailable" : date is not null ? "available" : "not_reported", fundamentals.Error));
        }
        var undated = events.Count(e => e.Date is null && (from is not null || to is not null));
        var filtered = events.DistinctBy(e => e.Id).Where(e => (from is null && to is null) || e.Date is { } d && (from is null || (e.EndDate ?? d) >= from) && (to is null || d <= to))
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Published).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var available = coverage.Count(c => c.Status != "unavailable");
        return new(symbol, filtered.Take(limit).ToArray(), coverage, available == 0 ? "unavailable" : available == coverage.Count ? "complete" : "partial", now, filtered.Length > limit, undated);
    }
    public static bool IsSafeUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(uri.UserInfo);
    private static string Id(params string?[] fields) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", fields))));
    private static async Task<(T? Value, string? Error)> Read<T>(Func<Task<T>> read, CancellationToken ct) where T : class
    {
        try { return (await read().WaitAsync(ct), null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLog.Warn("timeline", "Provider timeline component unavailable: " + ex.GetType().Name); return (null, ex is OperationCanceledException ? "provider_timeout" : "provider_unavailable"); }
    }
}
