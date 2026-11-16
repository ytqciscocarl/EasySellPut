using System.ComponentModel;
using System.Windows.Controls;
using Meili.App.Services;

namespace Meili.App.Views.Pages;

public partial class ChartsPage : Page
{
    private readonly ThemeService _theme = AppServices.Get<ThemeService>();
    private ChartsViewModel Vm => (ChartsViewModel)DataContext;
    private bool _active;
    public ChartsPage()
    {
        InitializeComponent();
        var vm = AppServices.Get<ChartsViewModel>(); DataContext = vm;
        Loaded += async (_, _) =>
        {
            if (_active) return;
            _active = true;
            vm.PlotChanged += OnPlotChanged; vm.PropertyChanged += OnPropertyChanged;
            ThemeService.ColorsChanged += OnPlotChanged;
            Chart.Start(vm, WpfChartPalette.Current(_theme.IsDark));
            await vm.ActivateAsync(); Render();
        };
        Unloaded += (_, _) =>
        {
            _active = false; vm.PlotChanged -= OnPlotChanged; vm.PropertyChanged -= OnPropertyChanged;
            ThemeService.ColorsChanged -= OnPlotChanged;
            vm.Deactivate(); Chart.Stop();
        };
    }
    private void OnPlotChanged(object? sender, EventArgs e) => Render();
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ChartsViewModel.DrawMode)) Render(); }
    private void Render()
    { if (_active) Chart.Update(WpfChartPalette.Current(_theme.IsDark)); }
}
