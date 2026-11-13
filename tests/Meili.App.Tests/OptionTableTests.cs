using System.Windows.Controls;
using Meili.App.Views.Pages;
using Meili.Core.Settings;
using Meili.App.Services;

namespace Meili.App.Tests;

/// <summary>G4 + H1: data-driven T-table columns, spread, OI bars, distance to spot.</summary>
public class OptionTableTests
{
    [Fact]
    public void Columns_mirror_around_the_strike_and_follow_the_toggles()
    {
        var all = new OptionColumnSet();
        var calls = OptionColumns.Side(true, all).Select(c => c.Key).ToList();
        var puts = OptionColumns.Side(false, all).Select(c => c.Key).ToList();
        Assert.Equal(["Volume", "Oi", "OiChange", "Iv", "Delta", "Gamma", "Theta", "Vega", "SpreadPct", "Bid", "Ask", "Last"], calls);
        Assert.Equal(Enumerable.Reverse(calls), puts);
        Assert.Equal(["Strike", "Distance"], OptionColumns.Middle(all).Select(c => c.Key));

        var lean = new OptionColumnSet(Volume: false, Gamma: false, Spread: false, Distance: false);
        Assert.DoesNotContain("OiChange", OptionColumns.Side(true, all with { OiChange = false }).Select(c => c.Key));
        Assert.DoesNotContain("Volume", OptionColumns.Side(true, lean).Select(c => c.Key));
        Assert.DoesNotContain("SpreadPct", OptionColumns.Side(false, lean).Select(c => c.Key));
        Assert.Equal(["Strike"], OptionColumns.Middle(lean).Select(c => c.Key));
        Assert.True(OptionColumns.TotalWidth(all with { Compact = true }) < OptionColumns.TotalWidth(all));
    }

    [Theory]
    [InlineData(2.00, 2.20, false)]   // 9.5 % → fine
    [InlineData(0.10, 0.20, true)]    // 67 % and 0.10 wide
    [InlineData(0.01, 0.03, false)]   // huge %, but only 0.02 wide
    public void Wide_spread_rule(double bid, double ask, bool wide)
    {
        var (spread, pct, isWide) = EnrichedSide.SpreadOf(bid, ask);
        Assert.Equal(ask - bid, spread!.Value, 9);
        Assert.Equal((ask - bid) / ((ask + bid) / 2), pct!.Value, 9);
        Assert.Equal(wide, isWide);
        Assert.Equal((null, null, false), EnrichedSide.SpreadOf(null, 1));
    }

    [Fact]
    public void Distance_to_spot_and_oi_ratios()
    {
        var row = new OptionStrikeRow(105, null, null, false, 100);
        Assert.Equal("+5.0%", row.DistanceText);
        Assert.Equal("-10.0%", new OptionStrikeRow(90, null, null, false, 100).DistanceText);
        Assert.Equal("—", new OptionStrikeRow(90, null, null, false, null).DistanceText);

        var rows = new[]
        {
            new OptionStrikeRow(100, new EnrichedSide { OiValue = 500 }, new EnrichedSide { OiValue = 40 }, false, 100),
            new OptionStrikeRow(105, new EnrichedSide { OiValue = 1000 }, new EnrichedSide { OiValue = null }, false, 100),
        };
        OptionStrikeRow.SetOiRatios(rows);
        Assert.Equal(0.5, rows[0].Call!.OiRatio);
        Assert.Equal(1.0, rows[1].Call!.OiRatio);
        Assert.Equal(1.0, rows[0].Put!.OiRatio);
        Assert.Equal(0.0, rows[1].Put!.OiRatio);
    }

    [Fact]
    public void Loaded_chain_has_spreads_and_distances()
    {
        WpfHost.Run(async () =>
        {
            var vm = AppServices.Get<OptionsViewModel>();
            try
            {
                vm.SymbolText = "MSFT";
                await vm.LoadAsync();
                var atm = vm.Rows.First(r => r.Strike == 125);
                Assert.Equal("0.20", atm.Call!.Spread);
                Assert.NotEqual("—", atm.Call.SpreadPct);
                Assert.Equal("+1.3%", atm.DistanceText);                 // (125 − 123.45) / 123.45
                Assert.All(vm.Rows, r => Assert.Equal(1.0, r.Call!.OiRatio)); // fake chain: equal OI everywhere
            }
            finally { vm.Deactivate(); }
        });
    }
}

/// <summary>E6: the compact preset fits a 1920×1080 laptop at 150 % (1280 logical px, navigation pane open).</summary>
public class CompactColumnsTests
{
    [Fact]
    public void Compact_preset_fits_1280_logical_at_150_percent()
    {
        WpfHost.Run(async () =>
        {
            var vm = AppServices.Get<OptionsViewModel>();
            var store = AppServices.Get<SettingsStore>();
            try
            {
                vm.SymbolText = "SPY";
                var page = new OptionsPage();
                using var host = new OffscreenHost(page, 1280 - 232, 720);   // window width minus the open navigation pane
                await ScreenRenderTests.Settle(2);
                await vm.LoadAsync();
                double Overflow()
                {
                    host.Layout();
                    var sv = UiAudit.Descendants<ScrollViewer>((ListView)page.FindName("ChainList")).First();
                    return sv.ExtentWidth - sv.ViewportWidth;
                }
                vm.CompactColumns = false;
                Assert.True(Overflow() > 0, "the full table is expected to need horizontal scrolling at this width");
                vm.CompactColumns = true;
                await ScreenRenderTests.Settle(2);
                Assert.True(Overflow() <= 0, $"compact table overflows by {Overflow():0} px");
                Assert.False(vm.ShowVolume || vm.ShowGamma || vm.ShowTheta || vm.ShowVega);
                Assert.True(store.Current.Options.CompactColumns);
                if (Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is { Length: > 0 } dir)
                    host.SavePng(System.IO.Path.Combine(dir, "options-compact-150.png"), 1.5);
            }
            finally
            {
                vm.CompactColumns = false;
                vm.Deactivate();
            }
        });
    }
}

/// <summary>B6: IV Rank from recorded history, ATM term structure, and OI change against the previous stored day.</summary>
public class VolatilityInsightsTests
{
    [Fact]
    public void Options_page_shows_iv_rank_term_structure_and_oi_change()
    {
        WpfHost.Run(async () =>
        {
            var vm = AppServices.Get<OptionsViewModel>();
            var db = AppServices.Get<Meili.Data.Cache.MarketDb>();
            var loc = AppServices.Get<LocalizationService>();
            var now = DateTimeOffset.Now;
            var today = DateOnly.FromDateTime(Meili.Core.Time.MarketClock.ToEastern(now).Date);
            const string sym = "IVRK";
            try
            {
                for (var i = 1; i <= 30; i++)                                           // 30 past days: 0.10 … 0.40
                    db.AddIvSample(sym, now.AddDays(-i), "Iv30", 0.10 + (30 - i) * 0.01, "fake");
                db.SaveOpenInterest(sym, today.AddDays(-1), [($"{sym}{vm.Expirations.FirstOrDefault()?.Date ?? default:yyMMdd}C00125000", 0)]);
                WpfHost.Iv.Iv = 0.25;
                vm.SymbolText = sym;
                await vm.LoadAsync();
                for (var i = 0; i < 40 && vm.TermPoints.Count == 0; i++) await ScreenRenderTests.Settle(1);

                Assert.StartsWith("IV Rank", vm.IvRankText);
                Assert.Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0}", (0.25 - 0.10) / (0.39 - 0.10) * 100), vm.IvRankText);
                Assert.Equal(2, vm.TermPoints.Count);                                   // the fake chain lists two expirations
                Assert.All(vm.TermPoints, p => Assert.Equal(0.25, p.Iv, 2));

                // Today's OI was stored on this load; a load "tomorrow" compares with it.
                var prev = await AppServices.Get<VolatilityInsights>().SwapOpenInterestAsync(sym, today.AddDays(1), []);
                Assert.Equal(1000, prev.Values.First());
                var row = vm.Rows.First(r => r.Strike == 125);
                Assert.Equal("—", row.Put!.OiChange);                                     // no earlier day for the put
            }
            finally
            {
                db.PruneIvSamples(now.AddDays(1));
                db.PruneOpenInterest(today.AddDays(5));
                vm.Deactivate();
            }
        });
    }

    [Fact]
    public void Oi_change_text_is_signed_and_uses_amount_units()
    {
        WpfHost.Run(() =>
        {
            var loc = AppServices.Get<LocalizationService>();
            var c = new Meili.Core.Models.OptionContract
            {
                ContractSymbol = "X", Underlying = "X", Right = Meili.Core.Models.OptionRight.Call, Strike = 1, Expiration = new DateOnly(2027, 1, 1), OpenInterest = 25_000,
            };
            var e = Meili.Analytics.OptionEnricher.Enrich(Meili.Analytics.OptionRight.Call, 1, 1, new DateOnly(2027, 1, 1), null, null, null, null,
                null, null, null, null, null, null, null, new Meili.Analytics.OptionEnrichmentSettings());
            Assert.Equal("+" + loc.Amount(5000), EnrichedSide.From(c, e, loc, 20_000).OiChange);
            Assert.Equal("−" + loc.Amount(5000), EnrichedSide.From(c, e, loc, 30_000).OiChange);
            Assert.Equal("—", EnrichedSide.From(c, e, loc).OiChange);
            return Task.CompletedTask;
        });
    }
}
