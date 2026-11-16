using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Meili.Mac.Platform;

namespace Meili.Mac.Views;

public partial class ChartsPage : UserControl
{
    private readonly MacTheme _theme = AppServices.Get<MacTheme>();
    private bool _active;
    public ChartsPage()
    {
        InitializeComponent();
        var vm = AppServices.Get<ChartsViewModel>(); DataContext = vm;
        AttachedToVisualTree += async (_, _) =>
        {
            if (_active) return;
            _active = true;
            vm.PlotChanged += OnPlotChanged; vm.PropertyChanged += OnPropertyChanged;
            Ui.Platform.ColorsChanged += OnPlotChanged;
            Chart.Start(vm, MacChartPalette.Current(_theme.IsDark));
            await vm.ActivateAsync(); Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _active = false; vm.PlotChanged -= OnPlotChanged; vm.PropertyChanged -= OnPropertyChanged;
            Ui.Platform.ColorsChanged -= OnPlotChanged;
            vm.Deactivate(); Chart.Stop();
        };
        CompareBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { vm.ApplyCompareCommand.Execute(null); e.Handled = true; } };
    }
    private void OnPlotChanged(object? sender, EventArgs e) => Render();
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ChartsViewModel.DrawMode)) Render(); }
    internal void Render()
    { if (_active) Chart.Update(MacChartPalette.Current(_theme.IsDark)); }
}
