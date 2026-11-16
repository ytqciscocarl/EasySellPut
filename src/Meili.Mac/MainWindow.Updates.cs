using System.ComponentModel;
using Meili.Mac.Views;
namespace Meili.Mac;
public partial class MainWindow
{
    private UpdateViewModel _windowUpdates = AppServices.Get<UpdateViewModel>();
    private UpdateDialog? _updateDialog;
    private void SetupUpdateNotifications()
    {
        UseUpdateState(_windowUpdates);
        Closed += (_, _) => { _windowUpdates.PropertyChanged -= OnUpdateNotificationChanged; _updateDialog?.Close(); };
    }
    internal void UseUpdateState(UpdateViewModel updates)
    {
        _windowUpdates.SetPresentationVisible(false);
        _windowUpdates.PropertyChanged -= OnUpdateNotificationChanged;
        _windowUpdates = updates;
        UpdateStatusIndicator.DataContext = updates;
        updates.PropertyChanged += OnUpdateNotificationChanged;
        updates.SetPresentationVisible(IsVisible && WindowState != Avalonia.Controls.WindowState.Minimized);
    }
    private void OnUpdateNotificationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpdateViewModel.IsUpdatePromptOpen)) return;
        if (!_windowUpdates.IsUpdatePromptOpen) { _updateDialog?.Close(); return; }
        if (_updateDialog is not null || !IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized) return;
        _updateDialog = new UpdateDialog(_windowUpdates);
        _updateDialog.Closed += (_, _) => { _updateDialog = null; _windowUpdates.DismissUpdatePromptCommand.Execute(null); };
        _updateDialog.Show(this);
    }
}
