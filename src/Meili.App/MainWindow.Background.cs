using System.Windows;
namespace Meili.App;
public partial class MainWindow
{
    private readonly DesktopActivity _windowActivity=AppServices.Get<DesktopActivity>();
    private readonly ShellViewModel _windowShell=AppServices.Get<ShellViewModel>();
    private readonly string _activityOwner="main-"+Guid.NewGuid();
    private bool _presentationSuspended;
    internal bool PresentationSuspended=>_presentationSuspended;
    private void SetupBackgroundLifecycle()
    {
        IsVisibleChanged+=(_,_)=>UpdateBackgroundLifecycle();StateChanged+=(_,_)=>UpdateBackgroundLifecycle();
        Closed+=(_,_)=>{_windowActivity.SetVisible(_activityOwner,false);_windowShell.SetPresentationVisible(false);_windowUpdates.SetPresentationVisible(false);};
    }
    private void UpdateBackgroundLifecycle()
    {
        var visible=IsVisible&&WindowState!=WindowState.Minimized;
        _windowActivity.SetVisible(_activityOwner,visible);_windowShell.SetPresentationVisible(visible);_windowUpdates.SetPresentationVisible(visible);
        if(!visible && !_presentationSuspended){_presentationSuspended=true;NativePageHost.Children.Remove(RootNavigation);}
        else if(visible && _presentationSuspended){_presentationSuspended=false;if(_activeWebsite is null && !NativePageHost.Children.Contains(RootNavigation))NativePageHost.Children.Add(RootNavigation);}
        if(_articleReader is not null)_ = _articleReader.SetPresentationActiveAsync(visible);
        foreach(var (id,pane) in _webTabs)_ = pane.SetPresentationActiveAsync(visible && id==_activeWebsite);
    }
    internal void PrepareBackgroundStartup()
    {
        _notify.Attach(this);
        if(!_notify.HasTrayIcon){ShowInTaskbar=true;WindowState=WindowState.Minimized;Show();}
        UpdateBackgroundLifecycle();
    }
    public void RunInBackground(){HideToTray();_notify.ShowHiddenToTray();}
    private void OnBackground(object sender,RoutedEventArgs e)=>RunInBackground();
}
