using Avalonia.Controls;
using Avalonia.Input;
using Meili.Mac.Platform;
using Meili.Mac.Views.Controls;
namespace Meili.Mac;
public partial class MainWindow
{
    private ArticleSplitView? _newsSplit;
    private WebTabPane? _articleReader;
    private Avalonia.Input.IInputElement? _newsFocus;
    internal ArticleSplitView? NewsSplit=>_newsSplit;
    private void SetupNewsSplit()
    {
        var main=(Panel)PageHost.Parent!;var root=(Grid)main.Parent!;
        root.Children.Remove(main);_newsSplit=new ArticleSplitView(main);Grid.SetColumn(_newsSplit,1);root.Children.Add(_newsSplit);
        AddHandler(KeyDownEvent,(_,e)=>{if(e.Key==Key.Escape&&_articleReader is not null){CloseNewsArticle();e.Handled=true;}},Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closed+=(_,_)=>CloseNewsArticle();
    }
    public void OpenNewsArticle(string url,string title)
    {
        if(!NewsReaderPolicy.IsAllowed(url))throw new ArgumentException("Unsupported article URL",nameof(url));
        if(!IsVisible||WindowState==WindowState.Minimized)ShowFromDock();
        if(_articleReader is null)
        {
            _newsFocus=FocusManager?.GetFocusedElement();
            _articleReader=new WebTabPane(url,articleMode:true);
            _articleReader.CloseRequested+=CloseNewsArticle;
        }
        else _articleReader.OpenArticle(url);
        Avalonia.Automation.AutomationProperties.SetName(_articleReader,title);
        _newsSplit!.Open(_articleReader);
    }
    public void CloseNewsArticle()
    {
        if(_articleReader is null)return;
        _articleReader.CloseRequested-=CloseNewsArticle;
        _articleReader.SetPresentationActive(false);
        _newsSplit?.Close();_articleReader=null;
        if(IsVisible&&_newsFocus is Control {IsEffectivelyVisible:true} focus)focus.Focus();
        _newsFocus=null;
    }
}
