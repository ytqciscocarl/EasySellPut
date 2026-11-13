using Meili.Analytics;

namespace Meili.Analytics.Tests;

public class StrategyRiskTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mixed_expiries_do_not_report_a_defined_risk_margin(bool longExpiresFirst)
    {
        var first = new DateOnly(2026, 12, 18);
        var second = new DateOnly(2027, 1, 15);
        var strategy = new OptionStrategy([
            StrategyLeg.Call(PositionSide.Long, 100, 3, longExpiresFirst ? first : second, 1, 0.3),
            StrategyLeg.Call(PositionSide.Short, 100, 5, longExpiresFirst ? second : first, 1, 0.3)]);
        var estimate = StrategyRisk.RegTMargin(strategy, 100);
        Assert.True(double.IsNaN(estimate.Amount));
        Assert.False(estimate.DefinedRisk);
    }

    private static readonly DateOnly Exp = new(2027, 1, 15);
    private static readonly StrategyValuation Now = StrategyValuation.AtDate(new DateOnly(2026, 12, 16), 0.04, 0.01);

    private static double Lognormal(double x, double spot, double vol, double t, double r, double q) =>
        NormalDistribution.Cdf((Math.Log(x / spot) - (r - q - 0.5 * vol * vol) * t) / (vol * Math.Sqrt(t)));

    [Fact]
    public void Long_call_pop_is_the_chance_of_finishing_above_its_breakeven()
    {
        var s = new OptionStrategy([StrategyLeg.Call(PositionSide.Long, 100, 3, Exp)]);
        double t = Now.YearsTo(Exp);
        double expected = 1 - Lognormal(103, 100, 0.3, t, 0.04, 0.01);
        Assert.Equal(expected, StrategyRisk.ProbabilityOfProfit(s, 100, 0.3, Now), 9);
    }

    [Fact]
    public void Short_strangle_pop_is_the_mass_between_its_breakevens()
    {
        var s = new OptionStrategy([StrategyLeg.Put(PositionSide.Short, 90, 2, Exp), StrategyLeg.Call(PositionSide.Short, 110, 2, Exp)]);
        double t = Now.YearsTo(Exp);
        double expected = Lognormal(114, 100, 0.25, t, 0.04, 0.01) - Lognormal(86, 100, 0.25, t, 0.04, 0.01);
        Assert.Equal(expected, StrategyRisk.ProbabilityOfProfit(s, 100, 0.25, Now), 9);
        Assert.InRange(expected, 0.7, 0.99);
    }

    [Fact]
    public void Calendar_pop_is_a_probability_and_expired_or_stock_only_is_nan()
    {
        var cal = new OptionStrategy([
            StrategyLeg.Call(PositionSide.Short, 100, 3, Exp, 1, 0.3), StrategyLeg.Call(PositionSide.Long, 100, 5, Exp.AddDays(30), 1, 0.3)]);
        Assert.InRange(StrategyRisk.ProbabilityOfProfit(cal, 100, 0.3, Now), 0.2, 0.9);
        Assert.True(double.IsNaN(StrategyRisk.ProbabilityOfProfit(new OptionStrategy([StrategyLeg.Stock(PositionSide.Long, 100, 50)]), 50, 0.3, Now)));
        var after = StrategyValuation.AtDate(Exp.AddDays(1), 0.04);
        Assert.True(double.IsNaN(StrategyRisk.ProbabilityOfProfit(new OptionStrategy([StrategyLeg.Call(PositionSide.Long, 100, 3, Exp)]), 100, 0.3, after)));
    }

    [Fact]
    public void Defined_risk_margin_is_the_max_loss()
    {
        var credit = new OptionStrategy([StrategyLeg.Put(PositionSide.Short, 100, 2.5, Exp), StrategyLeg.Put(PositionSide.Long, 95, 1, Exp)]);
        Assert.Equal(new MarginEstimate(350, true), StrategyRisk.RegTMargin(credit, 102));
        var longCall = new OptionStrategy([StrategyLeg.Call(PositionSide.Long, 100, 3, Exp, 2)]);
        Assert.Equal(new MarginEstimate(600, true), StrategyRisk.RegTMargin(longCall, 100));
    }

    [Fact]
    public void Naked_and_covered_legs_use_reg_t_rules()
    {
        // Short put 95 @ 2, spot 100: 20 % × 100 − 5 OTM = 15 (> 10 % × 95) + 2 → 17 per share.
        var put = new OptionStrategy([StrategyLeg.Put(PositionSide.Short, 95, 2, Exp)]);
        Assert.Equal(new MarginEstimate(1700, false), StrategyRisk.RegTMargin(put, 100));
        // Far OTM call 150 @ 0.1: floor 10 % × 100 = 10 + 0.1.
        var call = new OptionStrategy([StrategyLeg.Call(PositionSide.Short, 150, 0.1, Exp)]);
        Assert.Equal(1010, StrategyRisk.RegTMargin(call, 100).Amount, 6);
        // Covered call: 50 % of 100 shares at 100; the call is covered.
        var covered = new OptionStrategy([StrategyLeg.Stock(PositionSide.Long, 100, 100), StrategyLeg.Call(PositionSide.Short, 105, 2, Exp)]);
        Assert.Equal(new MarginEstimate(5000, false), StrategyRisk.RegTMargin(covered, 100));
    }

    [Fact]
    public void Representative_volatility_weights_by_units()
    {
        var s = new OptionStrategy([StrategyLeg.Call(PositionSide.Long, 100, 3, Exp, 1, 0.2), StrategyLeg.Put(PositionSide.Short, 90, 1, Exp, 3, 0.4)]);
        Assert.Equal(0.35, StrategyRisk.RepresentativeVolatility(s, 0.3), 9);
        Assert.Equal(0.3, StrategyRisk.RepresentativeVolatility(new OptionStrategy([StrategyLeg.Call(PositionSide.Long, 100, 3, Exp)]), 0.3));
    }
}
