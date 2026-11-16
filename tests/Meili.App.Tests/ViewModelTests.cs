using Meili.App.Services;

namespace Meili.App.Tests;

/// <summary>View-model state machines against the fake data service.</summary>
public class ViewModelTests
{
    [Fact]
    public void Charts_load_produces_series_and_indicators()
    {
        WpfHost.Run(async () =>
        {
            var vm = AppServices.Get<ChartsViewModel>();
            vm.SymbolText = "aapl";
            await vm.LoadAsync();
            Assert.Equal("AAPL", vm.SymbolText);
            Assert.NotNull(vm.Series);
            Assert.NotNull(vm.Snapshot);
            Assert.Equal(vm.Series!.Bars.Count, vm.Snapshot!.Sma.Length);
            vm.Deactivate();
        });
    }

    [Fact]
    public void Charts_invalid_symbol_shows_message_and_clears()
    {
        WpfHost.Run(async () =>
        {
            var vm = AppServices.Get<ChartsViewModel>();
            vm.SymbolText = "!!";
            await vm.LoadAsync();
            Assert.Null(vm.Series);
            Assert.False(string.IsNullOrEmpty(vm.StatusText));
            vm.Deactivate();
        });
    }

    [Fact]
    public void Options_load_builds_rows_with_atm()
    {
        WpfHost.Run(async () =>
        {
            var vm = AppServices.Get<OptionsViewModel>();
            vm.SymbolText = "MSFT";
            await vm.LoadAsync();
            Assert.True(vm.HasRows);
            Assert.NotNull(vm.AtmRow);
            vm.Deactivate();
        });
    }

    [Fact]
    public void Strategy_load_builds_legs_and_curve()
    {
        WpfHost.Run(async () =>
        {
            var vm = AppServices.Get<StrategyViewModel>();
            vm.SymbolText = "MSFT";
            await vm.LoadAsync();
            // The shared desktop model intentionally preserves a previous manual draft.
            // This test exercises a newly applied preset, independently of test order.
            vm.SelectedPreset = vm.Presets.First(p => p.Kind == Meili.Analytics.StrategyKind.CoveredCall);
            vm.ApplyPresetCommand.Execute(null);
            Assert.NotEmpty(vm.Legs);
            Assert.True(vm.HasCurve);
            vm.Deactivate();
        });
    }
}
