using Meili.Core.Abstractions;
using Meili.Core.Models;

namespace Meili.Automation.Tests;

internal sealed class FakeMarket : IMarketDataService
{
    public DateOnly? LastChainExpiry { get; private set; }
    public OptionChain? ChainOverride { get; init; }
    public Task<IReadOnlyList<NewsItem>>? PendingNews { get; init; }
    public DateTimeOffset? Publication { get; init; } = new(2026,11,13,15,0,0,TimeSpan.Zero);
    public bool FailNews { get; init; }
    public bool UnknownPublication { get; init; }
    public bool FailFundamentals { get; init; }
    public bool DelayQuotes { get; init; }
    private static readonly DateTimeOffset At = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);
    public async Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default) { if (DelayQuotes) await Task.Delay(Timeout.InfiniteTimeSpan, ct); return [new() { Symbol = "AAPL", Price = 100, Source = "fixture", AsOf = At, FetchedAt = At, DelayMinutes = 15 }]; }
    public Task<BarSeries> GetBarsAsync(BarRequest r, CancellationToken ct = default) => Task.FromResult(new BarSeries { Symbol = r.Symbol, Interval = r.Interval, Source = "fixture", AsOf = At, FetchedAt = At, OfflineCache = true, Bars = [Bar.CloseOnly(At, 100, 1), new(At.AddDays(1), 100, 102, 99, 101, 2)] });
    public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string s, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DateOnly>>([new(2027, 6, 18)]);
    public Task<OptionChain> GetChainAsync(string s, DateOnly? exp = null, CancellationToken ct = default) { LastChainExpiry = exp; return Task.FromResult(ChainOverride ?? new OptionChain { Underlying = s, Source = "fixture", Expirations = [new(2027, 6, 18)], Contracts = [new() { Underlying = s, ContractSymbol = "put", Right = Meili.Core.Models.OptionRight.Put, Strike = 100, Expiration = new(2027, 6, 18) }, new() { Underlying = s, ContractSymbol = "call", Right = Meili.Core.Models.OptionRight.Call, Strike = 100, Expiration = new(2027, 6, 18) }] }); }
    public Task<Fundamentals> GetFundamentalsAsync(string s, CancellationToken ct = default) => FailFundamentals ? throw new HttpRequestException() : Task.FromResult(new Fundamentals { Symbol = s, Source = "fixture", AsOf = At, FetchedAt = At, NextEarningsDate = new(2026,11,20), EarningsDateStatus = EventDateStatus.Estimated, ExDividendDate = new(2026,11,13) });
    public Task<FinancialStatements> GetFinancialsAsync(string s, CancellationToken ct = default) => Task.FromResult(new FinancialStatements { Symbol = s, Source = "fixture", Lines = [] });
    public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string s, int count = 20, CancellationToken ct = default) => PendingNews ?? (FailNews ? throw new HttpRequestException() : Task.FromResult<IReadOnlyList<NewsItem>>([new() { Title = "fixture", Url = "https://example.com", Source = "fixture", Published = UnknownPublication ? null : Publication }]));
    public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string q, int limit = 10, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SymbolMatch>>([new("AAPL", "Apple", null, null, "fixture")]);
}
