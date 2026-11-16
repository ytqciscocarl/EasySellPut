using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
namespace Meili.Mac;
public partial class MainWindow
{
    private readonly DesktopActivity _windowActivity=AppServices.Get<DesktopActivity>();
    private readonly ShellViewModel _windowShell=AppServices.Get<ShellViewModel>();
    private readonly string _activityOwner="main-"+Guid.NewGuid();
    private bool _presentationSuspended;
    internal bool PresentationSuspended=>_presentationSuspended;
    private void SetupBackgroundLifecycle()
    {
        Opened+=(_,_)=>UpdateBackgroundLifecycle();
        PropertyChanged+=(_,e)=>{if(e.Property==IsVisibleProperty||e.Property==WindowStateProperty)UpdateBackgroundLifecycle();};
        Closed+=(_,_)=>{_windowActivity.SetVisible(_activityOwner,false);_windowShell.SetPresentationVisible(false);_windowUpdates.SetPresentationVisible(false);};
    }
    private void UpdateBackgroundLifecycle()
    {
        var visible=IsVisible&&WindowState!=WindowState.Minimized;
        _windowActivity.SetVisible(_activityOwner,visible);_windowShell.SetPresentationVisible(visible);_windowUpdates.SetPresentationVisible(visible);
        if(!visible && !_presentationSuspended){_presentationSuspended=true;PageHost.Content=null;_bannerTimer.Stop();}
        else if(visible && _presentationSuspended){_presentationSuspended=false;if(_activeWebsite is null && _pages.TryGetValue(CurrentPage,out var page))PageHost.Content=page;}
        _articleReader?.SetPresentationActive(visible);
        foreach(var (id,pane) in _webTabs)pane.SetPresentationActive(visible && id==_activeWebsite);
    }
    internal void PrepareBackgroundStartup()=>UpdateBackgroundLifecycle();
    public void RunInBackground()=>HideToDock();
    private void OnBackground(object? sender,RoutedEventArgs e)=>RunInBackground();
}
